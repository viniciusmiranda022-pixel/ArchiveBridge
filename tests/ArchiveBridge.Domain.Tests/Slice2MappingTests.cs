using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Tests;

public sealed class Slice2MappingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly ContentCodePage CodePage = new(1252);
    private const string Folder = "/ImportedPst_Proj_Wave1";

    private static WaveEntry E(string path, string pst, string mailbox, long size = 10, long items = 1) =>
        new(path, pst, new ArchiveRef(mailbox), size, items);

    private static MigrationWave ApprovedWave(params WaveEntry[] entries)
    {
        var wave = MigrationWave.Create(
            WaveId.New(),
            new TenantId(Guid.NewGuid()),
            new ProjectId(Guid.NewGuid()),
            new WaveName("W"),
            TargetRootFolder.ForWave("Proj", "Wave1"),
            new Sha256Hash("cfg"),
            new WaveSelection(entries.ToList()),
            Now);
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("do", Now);
        return wave;
    }

    private static MappingGenerationResult Generate(MigrationWave wave) =>
        MappingCsvGenerator.Generate(wave, CodePage, MappingPolicy.Default, MappingVersion.Initial, "do", Now);

    // ---- Esquema / code page / política ----

    [Fact]
    public void HeaderHasExactlyTenColumnsInCanonicalOrder()
    {
        Assert.Equal(10, MappingSchema.ColumnCount);
        Assert.Equal(
            "Workload,FilePath,Name,Mailbox,IsArchive,TargetRootFolder,ContentCodePage,SPFileContainer,SPManifestContainer,SPSiteUrl",
            MappingSchema.HeaderLine);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void ContentCodePageRejectsOutOfRange(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentCodePage(value));

    [Fact]
    public void DefaultPolicyAllowsOnlyItsCodePages()
    {
        Assert.True(MappingPolicy.Default.IsAllowed(new ContentCodePage(1252)));
        Assert.True(MappingPolicy.Default.IsAllowed(new ContentCodePage(65001)));
        Assert.False(MappingPolicy.Default.IsAllowed(new ContentCodePage(1250)));
        Assert.Throws<MappingGenerationException>(() => MappingPolicy.Default.EnsureAllowed(new ContentCodePage(1250)));
    }

    // ---- MappingRow ----

    [Fact]
    public void MappingRowRejectsReservedIngestiondataContainer()
    {
        var entry = E("/ingestiondata/a.pst", "a.pst", "u@contoso.com");
        Assert.Throws<ArgumentException>(() =>
            MappingRow.Create(entry, new TargetRootFolder(Folder), CodePage));
    }

    // ---- Gerador ----

    [Fact]
    public void GenerateProducesCanonicalHeaderAndRow()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var lines = Generate(wave).Document.Content.Split("\r\n");

        Assert.Equal(MappingSchema.HeaderLine, lines[0]);
        Assert.Equal($"Exchange,/src/a.pst,a.pst,u@contoso.com,TRUE,{Folder},1252,,,", lines[1]);
    }

    [Fact]
    public void GenerateIsDeterministicAndVersioned()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var first = Generate(wave);
        var second = Generate(wave);

        Assert.Equal(first.Document.ContentSha256, second.Document.ContentSha256);
        Assert.Equal(1, first.Version.Version.Value);
        Assert.Equal(1, first.Document.RowCount);
        Assert.Equal(MappingValidationOutcome.Passed, first.Version.Validation);
        Assert.Equal(MappingVersionStatus.Usable, first.Version.Status);
    }

    [Fact]
    public void GenerateRefusesWaveThatIsNotApproved()
    {
        var wave = MigrationWave.Create(
            WaveId.New(), new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()),
            new WaveName("W"), TargetRootFolder.ForWave("Proj", "Wave1"), new Sha256Hash("cfg"),
            new WaveSelection([E("/src/a.pst", "a.pst", "u@contoso.com")]), Now);
        Assert.Throws<MappingGenerationException>(() => Generate(wave));
    }

    [Fact]
    public void GenerateFailsClosedOnFormulaLikeAuthorizedValue()
    {
        var wave = ApprovedWave(E("=cmd/a.pst", "a.pst", "u@contoso.com"));
        Assert.Throws<MappingCsvInjectionException>(() => Generate(wave));
    }

    [Fact]
    public void GenerateRejectsMoreThanFiveHundredRows()
    {
        var entries = Enumerable.Range(0, 501)
            .Select(i => E($"/src/{i}.pst", $"{i}.pst", $"u{i}@contoso.com"))
            .ToArray();
        var wave = ApprovedWave(entries);
        Assert.Throws<MappingGenerationException>(() => Generate(wave));
    }

    [Fact]
    public void GenerateRejectsCodePageOutsidePolicy()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        Assert.Throws<MappingGenerationException>(() =>
            MappingCsvGenerator.Generate(wave, new ContentCodePage(1250), MappingPolicy.Default, MappingVersion.Initial, "do", Now));
    }

    [Fact]
    public void SupersedeMarksVersionUnusable()
    {
        var version = Generate(ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"))).Version;
        var superseded = version.Supersede();
        Assert.Equal(MappingVersionStatus.Superseded, superseded.Status);
        Assert.Throws<InvalidOperationException>(() => superseded.Supersede());
    }

    // ---- Validador ----

    private static MigrationWave TwoEntryWave() =>
        ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"), E("/src/b.pst", "b.pst", "v@contoso.com"));

    private static string ValidContent(MigrationWave wave) => Generate(wave).Document.Content;

    private static string[] Lines(string content) => content.Split("\r\n");

    private static string Rejoin(IEnumerable<string> lines) => string.Join("\r\n", lines);

    [Fact]
    public void ValidatorAcceptsFaithfullyGeneratedCsv()
    {
        var wave = TwoEntryWave();
        var result = MappingCsvValidator.Validate(ValidContent(wave), wave, CodePage, MappingPolicy.Default);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidatorRejectsTamperedHeader()
    {
        var wave = TwoEntryWave();
        var lines = Lines(ValidContent(wave));
        lines[0] = lines[0].Replace("Workload", "workload", StringComparison.Ordinal);
        var result = MappingCsvValidator.Validate(Rejoin(lines), wave, CodePage, MappingPolicy.Default);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidatorDetectsMailboxSwap()
    {
        var wave = TwoEntryWave();
        var tampered = ValidContent(wave).Replace("u@contoso.com", "attacker@contoso.com", StringComparison.Ordinal);
        Assert.False(MappingCsvValidator.Validate(tampered, wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorDetectsTargetRootFolderSwap()
    {
        var wave = TwoEntryWave();
        var tampered = ValidContent(wave).Replace(Folder, "/ImportedPst_Other_Wave9", StringComparison.Ordinal);
        Assert.False(MappingCsvValidator.Validate(tampered, wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorDetectsDroppedRow()
    {
        var wave = TwoEntryWave();
        var lines = Lines(ValidContent(wave)).ToList();
        lines.RemoveAt(2); // remove a segunda linha de dados
        Assert.False(MappingCsvValidator.Validate(Rejoin(lines), wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorDetectsExtraColumn()
    {
        var wave = TwoEntryWave();
        var lines = Lines(ValidContent(wave)).ToList();
        lines[1] += ",EXTRA";
        Assert.False(MappingCsvValidator.Validate(Rejoin(lines), wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorDetectsFormulaInjection()
    {
        var wave = TwoEntryWave();
        var tampered = ValidContent(wave).Replace("/src/a.pst", "=/src/a.pst", StringComparison.Ordinal);
        Assert.False(MappingCsvValidator.Validate(tampered, wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorDetectsNonEmptySharePointColumns()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var lines = Lines(ValidContent(wave)).ToList();
        var fields = lines[1].Split(',');
        fields[7] = "https://sp";
        lines[1] = string.Join(',', fields);
        Assert.False(MappingCsvValidator.Validate(Rejoin(lines), wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorDetectsUnauthorizedPstName()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var lines = Lines(ValidContent(wave)).ToList();
        var fields = lines[1].Split(',');
        fields[2] = "z.pst"; // nome fora do manifesto autorizado
        lines[1] = string.Join(',', fields);
        Assert.False(MappingCsvValidator.Validate(Rejoin(lines), wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorDetectsDuplicateRow()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var lines = Lines(ValidContent(wave)).ToList();
        lines.Insert(2, lines[1]); // duplica a linha de dados
        Assert.False(MappingCsvValidator.Validate(Rejoin(lines), wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorRejectsMoreThanFiveHundredDataRows()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var lines = Lines(ValidContent(wave));
        var row = lines[1];
        var many = new List<string> { lines[0] };
        many.AddRange(Enumerable.Repeat(row, 501));
        var result = MappingCsvValidator.Validate(Rejoin(many), wave, CodePage, MappingPolicy.Default);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("excede o limite", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsBom()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var withBom = "﻿" + ValidContent(wave);
        Assert.False(MappingCsvValidator.Validate(withBom, wave, CodePage, MappingPolicy.Default).IsValid);
    }

    [Fact]
    public void ValidatorRejectsCodePageOutsidePolicy()
    {
        var wave = ApprovedWave(E("/src/a.pst", "a.pst", "u@contoso.com"));
        var content = ValidContent(wave);
        Assert.False(MappingCsvValidator.Validate(content, wave, new ContentCodePage(1250), MappingPolicy.Default).IsValid);
    }
}
