using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// B4: a idempotência do mapping usa a IMPRESSÃO DIGITAL COMPLETA de geração (não só a seleção).
/// Mudar qualquer parâmetro (code page, seleção, pasta de destino, configuração, esquema, gerador ou
/// política) muda a impressão. B5: o documento reconstruído do artefato verifica o SHA-256.
/// </summary>
public sealed class Slice2FingerprintTests
{
    private static readonly Sha256Hash Cfg = new("aaaa");
    private static readonly Sha256Hash Sel = new("bbbb");
    private static readonly TargetRootFolder Folder = TargetRootFolder.ForWave("proj0001", "wave0001");

    private static MappingGenerationFingerprint Base() =>
        MappingGenerationFingerprint.Compute(Cfg, Sel, Folder, new ContentCodePage(1252), 1, 1, 1);

    [Fact]
    public void StableForSameInputs() => Assert.Equal(Base(), Base());

    [Fact]
    public void DiffersByContentCodePage()
    {
        var other = MappingGenerationFingerprint.Compute(Cfg, Sel, Folder, new ContentCodePage(65001), 1, 1, 1);
        Assert.NotEqual(Base(), other);
    }

    [Fact]
    public void DiffersByConfigurationHash()
    {
        var other = MappingGenerationFingerprint.Compute(new Sha256Hash("cccc"), Sel, Folder, new ContentCodePage(1252), 1, 1, 1);
        Assert.NotEqual(Base(), other);
    }

    [Fact]
    public void DiffersBySelectionHash()
    {
        var other = MappingGenerationFingerprint.Compute(Cfg, new Sha256Hash("dddd"), Folder, new ContentCodePage(1252), 1, 1, 1);
        Assert.NotEqual(Base(), other);
    }

    [Fact]
    public void DiffersByTargetRootFolder()
    {
        var other = MappingGenerationFingerprint.Compute(
            Cfg, Sel, TargetRootFolder.ForWave("proj0002", "wave0002"), new ContentCodePage(1252), 1, 1, 1);
        Assert.NotEqual(Base(), other);
    }

    [Fact]
    public void DiffersBySchemaGeneratorPolicyVersions()
    {
        Assert.NotEqual(Base(), MappingGenerationFingerprint.Compute(Cfg, Sel, Folder, new ContentCodePage(1252), 2, 1, 1));
        Assert.NotEqual(Base(), MappingGenerationFingerprint.Compute(Cfg, Sel, Folder, new ContentCodePage(1252), 1, 2, 1));
        Assert.NotEqual(Base(), MappingGenerationFingerprint.Compute(Cfg, Sel, Folder, new ContentCodePage(1252), 1, 1, 2));
    }

    [Fact]
    public void GeneratorBindsFingerprintDifferentByCodePage()
    {
        var wave = ApprovedWave();
        var a = MappingCsvGenerator.Generate(wave, new ContentCodePage(1252), MappingPolicy.Default, MappingVersion.Initial, "do", DateTimeOffset.UnixEpoch);
        var b = MappingCsvGenerator.Generate(wave, new ContentCodePage(65001), MappingPolicy.Default, MappingVersion.Initial, "do", DateTimeOffset.UnixEpoch);
        Assert.NotEqual(a.Version.Fingerprint, b.Version.Fingerprint);
    }

    [Fact]
    public void FromPersistedAcceptsMatchingHashAndRejectsTampered()
    {
        var wave = ApprovedWave();
        var generated = MappingCsvGenerator.Generate(wave, new ContentCodePage(1252), MappingPolicy.Default, MappingVersion.Initial, "do", DateTimeOffset.UnixEpoch);
        var bytes = generated.Document.GetBytes();

        var rebuilt = MappingDocument.FromPersisted(bytes, generated.Document.RowCount, generated.Document.ContentSha256);
        Assert.Equal(generated.Document.ContentSha256, rebuilt.ContentSha256);

        var tampered = bytes.ToArray();
        tampered[^1] ^= 0x01;
        Assert.Throws<MappingGenerationException>(() =>
            MappingDocument.FromPersisted(tampered, generated.Document.RowCount, generated.Document.ContentSha256));
    }

    private static MigrationWave ApprovedWave()
    {
        var entry = new WaveEntry("/src/a.pst", "a.pst", new ArchiveRef("u@contoso.com", new TargetArchiveId("u@contoso.com")), 10, 1);
        var wave = MigrationWave.Create(
            WaveId.New(),
            new ArchiveBridge.Domain.IdentityAndAccess.TenantId(Guid.NewGuid()),
            new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()),
            new WaveName("Onda"),
            Folder,
            Cfg,
            new WaveSelection([entry]),
            DateTimeOffset.UnixEpoch);
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("do", DateTimeOffset.UnixEpoch);
        return wave;
    }
}
