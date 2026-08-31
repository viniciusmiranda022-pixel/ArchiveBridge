using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Desfecho RESOLVIDO de UM critério de encerramento (§49), já composto pelo chamador (Application layer) a
/// partir de evidência canônica ou de atestação humana — este tipo NUNCA resolve evidência por si só. Reaproveita
/// <see cref="ReadinessControlStatus"/>/<see cref="ReadinessEvidenceReference"/> (mesmo vocabulário fail-closed
/// já aceito pelo Passo 1 — nunca um enum paralelo divergente).
/// </summary>
public sealed record MigrationCompletionCriterionResult
{
    private const int MaxReasonCodeLength = 200;

    private MigrationCompletionCriterionResult(
        MigrationCompletionCriterionId criterionId,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc)
    {
        CriterionId = criterionId;
        Status = status;
        Evidence = evidence;
        ReasonCode = reasonCode;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Identidade estável do critério.</summary>
    public MigrationCompletionCriterionId CriterionId { get; }

    /// <summary>Desfecho resolvido — nunca <see cref="ReadinessControlStatus.Pass"/> sem evidência real (invariante aplicado pelo factory).</summary>
    public ReadinessControlStatus Status { get; }

    /// <summary>Referência opaca à evidência usada para resolver <see cref="Status"/>.</summary>
    public ReadinessEvidenceReference Evidence { get; }

    /// <summary>Código curto e sanitizado explicando o desfecho — nunca segredo/PII.</summary>
    public string ReasonCode { get; }

    /// <summary>Instante em que a evidência subjacente foi observada/produzida.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Cria um resultado explícito para um critério — a única forma de obter <see cref="ReadinessControlStatus.Pass"/> é fornecer evidência real (<paramref name="evidence"/> diferente de <see cref="ReadinessEvidenceReference.None"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="reasonCode"/> tem aparência de segredo/PII, ou <paramref name="status"/> é <see cref="ReadinessControlStatus.Pass"/> sem evidência real.</exception>
    public static MigrationCompletionCriterionResult Create(
        MigrationCompletionCriterionId criterionId,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (status == ReadinessControlStatus.Pass && evidence.Kind == ReadinessEvidenceKind.None)
        {
            throw new ArgumentException(
                "Pass exige evidência real (Kind != None) — não é possível declarar um critério de encerramento satisfeito sem evidência.",
                nameof(status));
        }

        var normalizedReasonCode = EvidenceText.RequireSafeOptional(
            reasonCode, nameof(reasonCode), MaxReasonCodeLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p));

        return new MigrationCompletionCriterionResult(criterionId, status, evidence, normalizedReasonCode, observedAtUtc);
    }

    /// <summary>Resultado canônico fail-closed para um critério cuja evidência ainda não foi produzida.</summary>
    public static MigrationCompletionCriterionResult NotMeasured(MigrationCompletionCriterionId criterionId, string reasonCode, DateTimeOffset observedAtUtc) =>
        Create(criterionId, ReadinessControlStatus.NotMeasured, ReadinessEvidenceReference.None, reasonCode, observedAtUtc);
}
