using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I5-012 — o builder puro do mapping CSV do Purview: cabeçalho/esquema de 10 colunas reaproveitado do
/// Slice 2, <c>IsArchive</c> resolvido POR LINHA (nunca fixo), <c>ContentCodePage</c>/colunas SharePoint
/// sempre vazias, limite de 500 linhas fail-closed, nomes de PST únicos, serialização determinística
/// segura contra injeção de fórmula, e a impressão digital ligando o artefato à evidência exata que o
/// autorizou (mudança em qualquer entrada invalida a idempotência).
/// </summary>
public sealed class PurviewMappingCsvDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly TargetRootFolder Folder = TargetRootFolder.ForWave("prj01", "w001");

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static PurviewMappingRow Row(string name = "p_aaaa_part001.pst", string mailbox = "alice@contoso.com", bool isArchive = true) =>
        PurviewMappingRow.Create("tenant-project-wave", name, mailbox, isArchive, Folder);

    [Fact]
    public void GenerateProducesExactlyTheTenCanonicalColumnsInTheDocumentedOrderAndSpelling()
    {
        var result = PurviewMappingCsvGenerator.Generate(
            WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, [Row()], Hash("attempt"), MappingVersion.Initial, "operator", Now);

        var text = System.Text.Encoding.UTF8.GetString(result.Document.Bytes);
        var header = text.Split("\r\n")[0];

        Assert.Equal("Workload,FilePath,Name,Mailbox,IsArchive,TargetRootFolder,ContentCodePage,SPFileContainer,SPManifestContainer,SPSiteUrl", header);
    }

    [Fact]
    public void GenerateEmitsIsArchiveTrueOrFalsePerRowAndAlwaysLeavesContentCodePageAndSharePointColumnsEmpty()
    {
        var activeRow = Row("p_aaaa_part001.pst", "alice@contoso.com", isArchive: true);
        var inactiveRow = Row("p_bbbb_part001.pst", "bob@contoso.com", isArchive: false);

        var result = PurviewMappingCsvGenerator.Generate(
            WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, [activeRow, inactiveRow], Hash("attempt"), MappingVersion.Initial, "operator", Now);

        var lines = System.Text.Encoding.UTF8.GetString(result.Document.Bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var dataLines = lines[1..];

        Assert.Contains(dataLines, line => line.Contains("alice@contoso.com") && line.Split(',')[4] == "TRUE");
        Assert.Contains(dataLines, line => line.Contains("bob@contoso.com") && line.Split(',')[4] == "FALSE");
        Assert.All(dataLines, line =>
        {
            var fields = line.Split(',');
            Assert.Equal(string.Empty, fields[6]); // ContentCodePage
            Assert.Equal(string.Empty, fields[7]); // SPFileContainer
            Assert.Equal(string.Empty, fields[8]); // SPManifestContainer
            Assert.Equal(string.Empty, fields[9]); // SPSiteUrl
        });
    }

    [Fact]
    public void GenerateRejectsAnEmptyRowSet()
    {
        Assert.Throws<PurviewMappingCsvGenerationException>(() =>
            PurviewMappingCsvGenerator.Generate(
                WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, [], Hash("attempt"), MappingVersion.Initial, "operator", Now));
    }

    [Fact]
    public void GenerateAccepts500RowsButRejects501WithoutSilentSplitting()
    {
        var fiveHundredRows = Enumerable.Range(1, 500)
            .Select(i => Row($"p_{i:D4}_part001.pst", $"user{i}@contoso.com"))
            .ToList();

        var ok = PurviewMappingCsvGenerator.Generate(
            WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, fiveHundredRows, Hash("attempt"), MappingVersion.Initial, "operator", Now);
        Assert.Equal(500, ok.Document.RowCount);

        var fiveHundredOneRows = fiveHundredRows.Append(Row("p_0501_part001.pst", "user501@contoso.com")).ToList();
        Assert.Throws<PurviewMappingCsvGenerationException>(() =>
            PurviewMappingCsvGenerator.Generate(
                WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, fiveHundredOneRows, Hash("attempt"), MappingVersion.Initial, "operator", Now));
    }

    [Fact]
    public void GenerateRejectsADuplicatePstNameEvenAcrossDifferentMailboxes()
    {
        var duplicateName = "p_dupe_part001.pst";
        var rows = new[] { Row(duplicateName, "alice@contoso.com"), Row(duplicateName, "bob@contoso.com") };

        Assert.Throws<PurviewMappingCsvGenerationException>(() =>
            PurviewMappingCsvGenerator.Generate(
                WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, rows, Hash("attempt"), MappingVersion.Initial, "operator", Now));
    }

    [Fact]
    public void RowCreationRejectsAMailboxThatWouldBeInterpretedAsAFormulaByASpreadsheetTool()
    {
        Assert.Throws<ArgumentException>(() => PurviewMappingRow.Create("tenant-project-wave", "p_aaaa_part001.pst", "  ", true, Folder));
    }

    [Fact]
    public void SerializationFailsClosedWhenAnAuthorizedFieldWouldStartWithAFormulaTrigger()
    {
        // Mailbox é o único campo textual potencialmente vindo de diretório neste conjunto — um valor que
        // comece por '=' nunca é emitido reescrito/prefixado; a geração inteira é recusada.
        var row = PurviewMappingRow.Create("tenant-project-wave", "p_aaaa_part001.pst", "=cmd|'/ccalc'!A1@contoso.com", true, Folder);

        Assert.Throws<MappingCsvInjectionException>(() =>
            PurviewMappingCsvGenerator.Generate(
                WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, [row], Hash("attempt"), MappingVersion.Initial, "operator", Now));
    }

    [Fact]
    public void GenerateIsDeterministicProducingTheSameBytesAndHashForTheSameInput()
    {
        var wave = WaveId.New();
        var project = new ProjectId(Guid.NewGuid());
        var rows = new[] { Row("p_aaaa_part001.pst", "alice@contoso.com"), Row("p_bbbb_part001.pst", "bob@contoso.com") };

        var first = PurviewMappingCsvGenerator.Generate(wave, project, Folder, rows, Hash("attempt"), MappingVersion.Initial, "operator", Now);
        var second = PurviewMappingCsvGenerator.Generate(wave, project, Folder, rows, Hash("attempt"), MappingVersion.Initial, "operator", Now);

        Assert.Equal(first.Document.ContentSha256, second.Document.ContentSha256);
        Assert.Equal(first.Evidence.Fingerprint, second.Evidence.Fingerprint);
    }

    [Fact]
    public void GenerateProducesTheSameFingerprintRegardlessOfTheOrderRowsWereSuppliedIn()
    {
        var wave = WaveId.New();
        var project = new ProjectId(Guid.NewGuid());
        var a = Row("p_aaaa_part001.pst", "alice@contoso.com");
        var b = Row("p_bbbb_part001.pst", "bob@contoso.com");

        var first = PurviewMappingCsvGenerator.Generate(wave, project, Folder, [a, b], Hash("attempt"), MappingVersion.Initial, "operator", Now);
        var second = PurviewMappingCsvGenerator.Generate(wave, project, Folder, [b, a], Hash("attempt"), MappingVersion.Initial, "operator", Now);

        Assert.Equal(first.Evidence.Fingerprint, second.Evidence.Fingerprint);
    }

    // AB-I5-016: o mapping é lido de SqlWavePartitionOutputBindingStore ordenado apenas por
    // CreatedAtUtc(DATETIME2(3)), que pode empatar entre bindings persistidos na mesma onda — SQL Server
    // não garante ordem relativa entre empates. O MESMO conjunto canônico de linhas, entregue em QUALQUER
    // ordem de entrada (aqui: ordem inversa e embaralhada), deve produzir bytes/SHA-256/fingerprint
    // IDÊNTICOS — não apenas o fingerprint lógico (já coberto acima), mas o CSV físico serializado.
    [Fact]
    public void GenerateProducesIdenticalBytesHashAndFingerprintRegardlessOfInputRowOrder()
    {
        var wave = WaveId.New();
        var project = new ProjectId(Guid.NewGuid());
        var a = Row("p_aaaa_part001.pst", "alice@contoso.com", isArchive: true);
        var b = Row("p_bbbb_part001.pst", "bob@contoso.com", isArchive: false);
        var c = Row("p_cccc_part001.pst", "carol@contoso.com", isArchive: true);

        var ascending = PurviewMappingCsvGenerator.Generate(
            wave, project, Folder, [a, b, c], Hash("attempt"), MappingVersion.Initial, "operator", Now);
        var reversed = PurviewMappingCsvGenerator.Generate(
            wave, project, Folder, [c, b, a], Hash("attempt"), MappingVersion.Initial, "operator", Now);
        var shuffled = PurviewMappingCsvGenerator.Generate(
            wave, project, Folder, [b, a, c], Hash("attempt"), MappingVersion.Initial, "operator", Now);

        Assert.Equal(ascending.Document.Bytes, reversed.Document.Bytes);
        Assert.Equal(ascending.Document.Bytes, shuffled.Document.Bytes);
        Assert.Equal(ascending.Document.ContentSha256, reversed.Document.ContentSha256);
        Assert.Equal(ascending.Document.ContentSha256, shuffled.Document.ContentSha256);
        Assert.Equal(ascending.Evidence.Fingerprint, reversed.Evidence.Fingerprint);
        Assert.Equal(ascending.Evidence.Fingerprint, shuffled.Evidence.Fingerprint);

        // A ordem física das linhas segue Name (Ordinal): a, b, c — nunca a ordem de entrada.
        var lines = System.Text.Encoding.UTF8.GetString(ascending.Document.Bytes)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var names = lines[1..].Select(line => line.Split(',')[2]).ToArray();
        Assert.Equal(["p_aaaa_part001.pst", "p_bbbb_part001.pst", "p_cccc_part001.pst"], names);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerateProducesADifferentFingerprintWhenTheUploadAttemptIdentityDiffersEvenWithIdenticalRows(bool changeAttempt)
    {
        var wave = WaveId.New();
        var project = new ProjectId(Guid.NewGuid());
        var rows = new[] { Row() };

        var baseline = PurviewMappingCsvGenerator.Generate(wave, project, Folder, rows, Hash("attempt-a"), MappingVersion.Initial, "operator", Now);
        var attemptHash = changeAttempt ? Hash("attempt-b") : Hash("attempt-a");
        var varied = PurviewMappingCsvGenerator.Generate(wave, project, Folder, rows, attemptHash, MappingVersion.Initial, "operator", Now);

        if (changeAttempt)
        {
            Assert.NotEqual(baseline.Evidence.Fingerprint, varied.Evidence.Fingerprint);
        }
        else
        {
            Assert.Equal(baseline.Evidence.Fingerprint, varied.Evidence.Fingerprint);
        }
    }

    [Fact]
    public void FromPersistedFailsClosedWhenTheBytesDoNotMatchTheExpectedHash()
    {
        Assert.Throws<PurviewMappingCsvIntegrityViolationException>(() =>
            PurviewMappingDocument.FromPersisted(
                System.Text.Encoding.UTF8.GetBytes("tampered"), 1, Hash("original-content")));
    }

    [Fact]
    public void FromPersistedSucceedsWhenTheBytesMatchTheExpectedHash()
    {
        var original = PurviewMappingCsvGenerator.Generate(
            WaveId.New(), new ProjectId(Guid.NewGuid()), Folder, [Row()], Hash("attempt"), MappingVersion.Initial, "operator", Now);

        var rehydrated = PurviewMappingDocument.FromPersisted(
            original.Document.Bytes, original.Document.RowCount, original.Document.ContentSha256);

        Assert.Equal(original.Document.ContentSha256, rehydrated.ContentSha256);
        Assert.Equal(original.Document.Bytes, rehydrated.Bytes);
    }
}
