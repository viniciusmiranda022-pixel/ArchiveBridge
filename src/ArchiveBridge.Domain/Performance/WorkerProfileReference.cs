namespace ArchiveBridge.Domain.Performance;

/// <summary>Perfil de worker descrito no runbook §46 (dimensionamento/capacity planning).</summary>
public enum WorkerProfileKind
{
    /// <summary>Inspeção de PST (hash/estrutura de cabeçalho) — PSTs até ~100 GB.</summary>
    Inspector,

    /// <summary>PST pesado — 100–500+ GB, repair/split.</summary>
    HeavyPst,

    /// <summary>Validação independente (scan/hash).</summary>
    Validator,

    /// <summary>Upload/rede (AzCopy).</summary>
    Upload,
}

/// <summary>
/// Um perfil de referência de worker (runbook §46, tabela de dimensionamento): CPU/RAM/scratch citados
/// como ESTIMATIVA, nunca como mínimo garantido (AB-I7-003 §3). <see cref="MinScratchBytes"/>/
/// <see cref="MaxScratchBytes"/> são <see langword="null"/> quando o runbook não atribui um número
/// (ex.: Upload — "cache mínimo") — nunca um valor inventado para preencher a lacuna.
/// </summary>
public sealed record WorkerProfileReference(
    WorkerProfileKind Kind,
    int MinVCpu,
    int MaxVCpu,
    long MinRamBytes,
    long MaxRamBytes,
    long? MinScratchBytes,
    long? MaxScratchBytes,
    string TypicalUse)
{
    /// <summary>
    /// Nota fixa que acompanha todo perfil deste catálogo: nunca é um mínimo garantido, é referência do
    /// runbook (§46: "são estimativas, não mínimos garantidos").
    /// </summary>
    public const string ReferenceNotice =
        "Estimativa de referência do runbook (docs/runbook/06-parte-vi-plano-desenvolvimento.md §46) — NÃO é mínimo garantido nem SLA.";
}

/// <summary>
/// Catálogo fechado dos quatro perfis de worker do runbook §46, materializados como constantes versionadas
/// (não como configuração externa) — uma mudança nestes valores só pode vir de uma revisão explícita desta
/// fonte de verdade, nunca de inferência em runtime.
/// </summary>
public static class WorkerProfileCatalog
{
    private const long GiB = 1_073_741_824L; // GiB binário (2^30) — unidade usada pelo runbook para RAM/scratch destes perfis.
    private const long TiB = GiB * 1024;

    /// <summary>Inspector: 8 vCPU / 32 GiB / 512 GiB scratch — PSTs até ~100 GB.</summary>
    public static readonly WorkerProfileReference Inspector = new(
        WorkerProfileKind.Inspector,
        MinVCpu: 8, MaxVCpu: 8,
        MinRamBytes: 32 * GiB, MaxRamBytes: 32 * GiB,
        MinScratchBytes: 512 * GiB, MaxScratchBytes: 512 * GiB,
        TypicalUse: "PSTs até ~100 GB");

    /// <summary>Heavy PST: 16–32 vCPU / 64–128 GiB / 1–2 TiB NVMe/SSD — 100–500+ GB, repair/split.</summary>
    public static readonly WorkerProfileReference HeavyPst = new(
        WorkerProfileKind.HeavyPst,
        MinVCpu: 16, MaxVCpu: 32,
        MinRamBytes: 64 * GiB, MaxRamBytes: 128 * GiB,
        MinScratchBytes: 1 * TiB, MaxScratchBytes: 2 * TiB,
        TypicalUse: "100–500+ GB, repair/split");

    /// <summary>Validator: 4–8 vCPU / 16–32 GiB / 256–512 GiB — scan/hash independente.</summary>
    public static readonly WorkerProfileReference Validator = new(
        WorkerProfileKind.Validator,
        MinVCpu: 4, MaxVCpu: 8,
        MinRamBytes: 16 * GiB, MaxRamBytes: 32 * GiB,
        MinScratchBytes: 256 * GiB, MaxScratchBytes: 512 * GiB,
        TypicalUse: "independent scan/hash");

    /// <summary>Upload: 4–8 vCPU / 16 GiB / cache mínimo (sem número atribuído pelo runbook) — AzCopy/rede.</summary>
    public static readonly WorkerProfileReference Upload = new(
        WorkerProfileKind.Upload,
        MinVCpu: 4, MaxVCpu: 8,
        MinRamBytes: 16 * GiB, MaxRamBytes: 16 * GiB,
        MinScratchBytes: null, MaxScratchBytes: null,
        TypicalUse: "AzCopy/network (cache mínimo — runbook não atribui bytes)");

    /// <summary>Todos os perfis, na ordem em que aparecem no runbook §46.</summary>
    public static IReadOnlyList<WorkerProfileReference> All { get; } = [Inspector, HeavyPst, Validator, Upload];
}
