using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>
/// AB-I7-003 §3 — os quatro perfis do runbook §46 materializados como referência, nunca como mínimo
/// garantido. Fixa os valores exatos da tabela do runbook para que uma divergência futura (número mudado
/// sem atualizar o runbook, ou vice-versa) quebre este teste em vez de passar silenciosamente.
/// </summary>
public sealed class WorkerProfileCatalogTests
{
    private const long GiB = 1_073_741_824L;
    private const long TiB = GiB * 1024;

    [Fact]
    public void InspectorMatchesRunbookTable()
    {
        var profile = WorkerProfileCatalog.Inspector;

        Assert.Equal(WorkerProfileKind.Inspector, profile.Kind);
        Assert.Equal(8, profile.MinVCpu);
        Assert.Equal(8, profile.MaxVCpu);
        Assert.Equal(32 * GiB, profile.MinRamBytes);
        Assert.Equal(32 * GiB, profile.MaxRamBytes);
        Assert.Equal(512 * GiB, profile.MinScratchBytes);
        Assert.Equal(512 * GiB, profile.MaxScratchBytes);
    }

    [Fact]
    public void HeavyPstMatchesRunbookTable()
    {
        var profile = WorkerProfileCatalog.HeavyPst;

        Assert.Equal(16, profile.MinVCpu);
        Assert.Equal(32, profile.MaxVCpu);
        Assert.Equal(64 * GiB, profile.MinRamBytes);
        Assert.Equal(128 * GiB, profile.MaxRamBytes);
        Assert.Equal(1 * TiB, profile.MinScratchBytes);
        Assert.Equal(2 * TiB, profile.MaxScratchBytes);
    }

    [Fact]
    public void ValidatorMatchesRunbookTable()
    {
        var profile = WorkerProfileCatalog.Validator;

        Assert.Equal(4, profile.MinVCpu);
        Assert.Equal(8, profile.MaxVCpu);
        Assert.Equal(16 * GiB, profile.MinRamBytes);
        Assert.Equal(32 * GiB, profile.MaxRamBytes);
        Assert.Equal(256 * GiB, profile.MinScratchBytes);
        Assert.Equal(512 * GiB, profile.MaxScratchBytes);
    }

    [Fact]
    public void UploadHasNoFabricatedScratchNumberBecauseTheRunbookGivesNone()
    {
        var profile = WorkerProfileCatalog.Upload;

        Assert.Equal(4, profile.MinVCpu);
        Assert.Equal(8, profile.MaxVCpu);
        Assert.Equal(16 * GiB, profile.MinRamBytes);
        Assert.Equal(16 * GiB, profile.MaxRamBytes);
        Assert.Null(profile.MinScratchBytes);
        Assert.Null(profile.MaxScratchBytes);
    }

    [Fact]
    public void CatalogExposesExactlyTheFourRunbookProfilesInOrder()
    {
        Assert.Equal(
            [WorkerProfileKind.Inspector, WorkerProfileKind.HeavyPst, WorkerProfileKind.Validator, WorkerProfileKind.Upload],
            WorkerProfileCatalog.All.Select(profile => profile.Kind));
    }

    [Fact]
    public void ReferenceNoticeIsFixedAndNeverEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(WorkerProfileReference.ReferenceNotice));
        Assert.Contains("NÃO é mínimo garantido", WorkerProfileReference.ReferenceNotice, StringComparison.Ordinal);
    }
}
