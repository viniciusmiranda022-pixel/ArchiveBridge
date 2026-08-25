using System.Text;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I6-001 — <see cref="PurviewServiceResultReportParser"/> (bounded/estrito/fail-closed),
/// <see cref="PurviewServiceResultCorrelation"/> (1:1 com a cadeia canônica) e
/// <see cref="PurviewServiceResultCompleteness"/> (CompleteForProviderEvidence/Incomplete/Inconclusive —
/// nunca PASS/certificate). Campo ausente permanece <see langword="null"/> (Unknown/NotReported), nunca
/// zero.
/// </summary>
public sealed class PurviewServiceResultDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static PurviewRemotePstName RemoteName(int seed = 1) => PurviewRemotePstName.ForPart(ArtifactIdFromSeed(seed), 1);

    private static ArtifactId ArtifactIdFromSeed(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        return new ArtifactId(new Guid(bytes));
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    [Fact]
    public void ParseAcceptsAHappyPathReportAndNormalizesEveryRecognizedColumn()
    {
        var name = RemoteName().Value;
        var content = Utf8($"RemotePstName,Status,ImportedItemCount,ImportedSizeBytes,SkippedItemCount,CorruptedItemCount\n{name},Succeeded,10,2048,0,0\n");

        var result = PurviewServiceResultReportParser.Parse(content);

        var row = Assert.Single(result.Rows);
        Assert.Equal(name, row.RemoteName.Value);
        Assert.Equal(PurviewServiceResultRowStatus.Succeeded, row.Status);
        Assert.Equal(10, row.ImportedItemCount);
        Assert.Equal(2048, row.ImportedSizeBytes);
        Assert.Equal(0, row.SkippedItemCount);
        Assert.Equal(0, row.CorruptedItemCount);
    }

    [Fact]
    public void ParseLeavesAMissingColumnAsNullNeverAsZero()
    {
        var name = RemoteName().Value;
        var content = Utf8($"RemotePstName\n{name}\n");

        var result = PurviewServiceResultReportParser.Parse(content);

        var row = Assert.Single(result.Rows);
        Assert.Equal(PurviewServiceResultRowStatus.Unknown, row.Status);
        Assert.Null(row.ImportedItemCount);
        Assert.Null(row.ImportedSizeBytes);
        Assert.Null(row.SkippedItemCount);
        Assert.Null(row.CorruptedItemCount);
    }

    [Fact]
    public void ParseLeavesAnEmptyCellInAnExistingColumnAsNullNeverAsZero()
    {
        var name = RemoteName().Value;
        var content = Utf8($"RemotePstName,ImportedItemCount\n{name},\n");

        var result = PurviewServiceResultReportParser.Parse(content);

        Assert.Null(Assert.Single(result.Rows).ImportedItemCount);
    }

    [Fact]
    public void ParseRejectsAnEmptyReport()
    {
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void ParseRejectsAReportLargerThanTheByteLimit()
    {
        var oversized = new byte[PurviewServiceResultReportParser.MaxReportBytes + 1];
        Array.Fill(oversized, (byte)'a');

        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(oversized));
    }

    [Fact]
    public void ParseRejectsAReportWithMoreDataRowsThanTheLimit()
    {
        var builder = new StringBuilder("RemotePstName\n");
        for (var i = 0; i < PurviewServiceResultReportParser.MaxDataRows + 1; i++)
        {
            builder.Append(RemoteName(i + 1).Value).Append('\n');
        }

        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(Utf8(builder.ToString())));
    }

    [Fact]
    public void ParseRejectsInvalidUtf8Bytes()
    {
        byte[] invalidUtf8 = [0x52, 0x65, 0x6D, 0xFF, 0xFE, 0x0A];
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(invalidUtf8));
    }

    [Fact]
    public void ParseRejectsAnEmbeddedNulByte()
    {
        var content = Utf8("RemotePstName\n").Concat([(byte)0]).ToArray();
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsAnUnrecognizedHeaderColumn()
    {
        var content = Utf8($"RemotePstName,TotallyUnknownColumn\n{RemoteName().Value},x\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsAHeaderMissingTheIdentityColumn()
    {
        var content = Utf8("Status\nSucceeded\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsADuplicateHeaderColumn()
    {
        var content = Utf8($"RemotePstName,RemotePstName\n{RemoteName().Value},x\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsARowWithAFieldCountDifferentFromTheHeader()
    {
        var content = Utf8($"RemotePstName,Status\n{RemoteName().Value}\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsARemoteNameThatDoesNotMatchTheDeterministicPattern()
    {
        var content = Utf8("RemotePstName\nnot-a-real-remote-name.pst\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsADuplicateRemoteNameWithinTheSameReport()
    {
        var name = RemoteName().Value;
        var content = Utf8($"RemotePstName\n{name}\n{name}\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsAMalformedNumericFieldRatherThanTreatingItAsUnknown()
    {
        var content = Utf8($"RemotePstName,ImportedItemCount\n{RemoteName().Value},not-a-number\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsANegativeNumericField()
    {
        var content = Utf8($"RemotePstName,ImportedItemCount\n{RemoteName().Value},-1\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseAcceptsATotalRowsDirectiveThatMatchesTheActualRowCount()
    {
        var content = Utf8($"#TotalRows:1\nRemotePstName\n{RemoteName().Value}\n");
        var result = PurviewServiceResultReportParser.Parse(content);

        Assert.Equal(1, result.DeclaredTotalRows);
    }

    [Fact]
    public void ParseRejectsATotalRowsDirectiveThatDisagreesWithTheActualRowCount()
    {
        var content = Utf8($"#TotalRows:2\nRemotePstName\n{RemoteName().Value}\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Fact]
    public void ParseRejectsAnUnrecognizedDirectiveLine()
    {
        var content = Utf8($"#SomethingElse:1\nRemotePstName\n{RemoteName().Value}\n");
        Assert.Throws<PurviewServiceResultParsingException>(() => PurviewServiceResultReportParser.Parse(content));
    }

    [Theory]
    [InlineData("Succeeded", PurviewServiceResultRowStatus.Succeeded)]
    [InlineData("Failed", PurviewServiceResultRowStatus.Failed)]
    [InlineData("SkippedOrCorrupted", PurviewServiceResultRowStatus.SkippedOrCorrupted)]
    [InlineData("SomeUnrecognizedProviderText", PurviewServiceResultRowStatus.Unknown)]
    public void ParseNormalizesStatusTextNeverInventingSuccessForAnUnrecognizedValue(string raw, PurviewServiceResultRowStatus expected)
    {
        var content = Utf8($"RemotePstName,Status\n{RemoteName().Value},{raw}\n");
        var row = Assert.Single(PurviewServiceResultReportParser.Parse(content).Rows);

        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public void RowConstructionRejectsANegativeCounter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PurviewServiceResultRow(RemoteName(), PurviewServiceResultRowStatus.Succeeded, -1, null, null, null));
    }

    [Fact]
    public void RowsHashIsIndependentOfInputOrder()
    {
        var rowA = new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Succeeded, 1, 2, 0, 0);
        var rowB = new PurviewServiceResultRow(RemoteName(2), PurviewServiceResultRowStatus.Failed, null, null, null, null);

        var forward = PurviewServiceResultRowsHash.Compute([rowA, rowB]);
        var reversed = PurviewServiceResultRowsHash.Compute([rowB, rowA]);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void RowsHashChangesWhenAnyCounterChanges()
    {
        var original = new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Succeeded, 1, 2, 0, 0);
        var tampered = new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Succeeded, 2, 2, 0, 0);

        Assert.NotEqual(PurviewServiceResultRowsHash.Compute([original]), PurviewServiceResultRowsHash.Compute([tampered]));
    }

    [Fact]
    public void CorrelateFailsClosedWhenARowReferencesAPstOutsideTheCurrentCanonicalSet()
    {
        var canonical = new[] { RemoteName(1) };
        var rows = new[] { new PurviewServiceResultRow(RemoteName(2), PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0) };

        Assert.Throws<PurviewServiceResultCorrelationException>(() =>
            PurviewServiceResultCorrelation.Correlate(canonical, rows, reportDeclaresCompleteness: false));
    }

    [Fact]
    public void CorrelateAllowsAPartialSubsetWhenTheReportNeverClaimedCompleteness()
    {
        var canonical = new[] { RemoteName(1), RemoteName(2) };
        var rows = new[] { new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0) };

        var result = PurviewServiceResultCorrelation.Correlate(canonical, rows, reportDeclaresCompleteness: false);

        Assert.Equal(2, result.CanonicalCount);
        Assert.Equal(1, result.MatchedCount);
    }

    [Fact]
    public void CorrelateFailsClosedWhenTheReportClaimsCompletenessButCoversOnlyASubset()
    {
        var canonical = new[] { RemoteName(1), RemoteName(2) };
        var rows = new[] { new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0) };

        Assert.Throws<PurviewServiceResultCorrelationException>(() =>
            PurviewServiceResultCorrelation.Correlate(canonical, rows, reportDeclaresCompleteness: true));
    }

    [Fact]
    public void CompletenessIsCompleteForProviderEvidenceOnlyWhenEveryCanonicalPstIsMatchedAndConclusive()
    {
        var canonical = new[] { RemoteName(1) };
        var rows = new[] { new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0) };
        var result = PurviewServiceResultCorrelation.Correlate(canonical, rows, reportDeclaresCompleteness: false);

        Assert.Equal(PurviewServiceResultCompletenessOutcome.CompleteForProviderEvidence, PurviewServiceResultCompleteness.Evaluate(result));
    }

    [Fact]
    public void CompletenessIsIncompleteWhenAnyCanonicalPstHasNoRowYet()
    {
        var canonical = new[] { RemoteName(1), RemoteName(2) };
        var rows = new[] { new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0) };
        var result = PurviewServiceResultCorrelation.Correlate(canonical, rows, reportDeclaresCompleteness: false);

        Assert.Equal(PurviewServiceResultCompletenessOutcome.Incomplete, PurviewServiceResultCompleteness.Evaluate(result));
    }

    [Fact]
    public void CompletenessIsInconclusiveWhenEveryPstIsMatchedButTheServiceDidNotExposeEnoughGranularity()
    {
        var canonical = new[] { RemoteName(1) };
        var rows = new[] { new PurviewServiceResultRow(RemoteName(1), PurviewServiceResultRowStatus.Unknown, null, null, null, null) };
        var result = PurviewServiceResultCorrelation.Correlate(canonical, rows, reportDeclaresCompleteness: false);

        Assert.Equal(PurviewServiceResultCompletenessOutcome.Inconclusive, PurviewServiceResultCompleteness.Evaluate(result));
    }

    [Fact]
    public void CompletenessNeverExposesAFinalPassOrCertificateOutcome()
    {
        var values = Enum.GetValues<PurviewServiceResultCompletenessOutcome>();
        Assert.DoesNotContain(values, value => value.ToString().Contains("Pass", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(values, value => value.ToString().Contains("Certificate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EvidenceRehydrateFailsClosedWhenTheContentHashWasTamperedAfterPersistence()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var name = PurviewImportJobName.Compute(tenant, project, wave, 1);
        var contentHash = DeterministicHash.Compute(["content"]);
        var rowsHash = DeterministicHash.Compute(["rows"]);
        var evidence = PurviewServiceResultReportEvidence.Create(
            tenant, project, wave, name, 1, contentHash, rowsHash, 100, 1, null, "operator", Now);
        var tamperedHash = DeterministicHash.Compute(["tampered"]);

        Assert.Throws<PurviewServiceResultIntegrityViolationException>(() =>
            PurviewServiceResultReportEvidence.Rehydrate(
                evidence.Tenant, evidence.Project, evidence.Wave, evidence.PlannedJobName, evidence.ReportVersion, tamperedHash,
                evidence.RowsSha256, evidence.RawSizeBytes, evidence.RowCount, evidence.DeclaredTotalRows, evidence.UploadedBy,
                evidence.CreatedAtUtc, evidence.EvidenceHash));
    }
}
