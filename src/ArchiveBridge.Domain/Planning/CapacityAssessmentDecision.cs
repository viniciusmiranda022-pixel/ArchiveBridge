using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Planning;

/// <summary>Desfecho de uma decisão de avaliação Microsoft para um archive acima do limite.</summary>
public enum MicrosoftAssessmentDecision
{
    /// <summary>A avaliação foi aprovada: o archive pode prosseguir apesar de exceder 100 GB.</summary>
    Approved = 0,

    /// <summary>A avaliação foi rejeitada: o bloqueio permanece.</summary>
    Rejected = 1,
}

/// <summary>
/// Registro imutável (append-only) de uma decisão de avaliação Microsoft sobre um archive específico
/// de uma versão de onda que excedeu 100 GB. Somente uma decisão <see cref="MicrosoftAssessmentDecision.Approved"/>
/// autorizada, vinculada à versão CORRENTE da onda e ao archive avaliado, pode liberar a onda
/// (<c>Blocked → Validating</c>) para revalidação. A decisão nunca altera nem apaga a avaliação
/// anterior; apenas adiciona evidência. Contém apenas metadados de governança — sem PII, segredo ou
/// conteúdo. O <see cref="EvidenceReference"/> é uma referência de evidência (ex.: id de ticket), não
/// um documento sensível.
/// </summary>
public sealed record CapacityAssessmentDecision(
    TargetArchiveId Archive,
    WaveVersion WaveVersion,
    MicrosoftAssessmentDecision Decision,
    string EvidenceReference,
    string DecidedBy,
    DateTimeOffset DecidedAtUtc,
    CorrelationId Correlation,
    string? Notes);
