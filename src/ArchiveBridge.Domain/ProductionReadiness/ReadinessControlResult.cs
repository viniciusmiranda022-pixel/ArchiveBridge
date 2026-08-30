using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Desfecho RESOLVIDO de UM controle do Production Readiness Review, já composto pelo chamador (Application
/// layer) a partir de evidência canônica — este tipo NUNCA resolve evidência por si só, apenas representa um
/// resultado já decidido (mesmo princípio de <see cref="ArchiveBridge.Domain.Security.SecurityReadinessSnapshot"/>:
/// read-model puro, não um agregado que busca dados). <see cref="ReasonCode"/> é validado com o mesmo guarda
/// fail-closed de <see cref="EvidenceText"/> — nunca segredo/PII.
/// </summary>
public sealed record ReadinessControlResult
{
    private const int MaxReasonCodeLength = 200;

    private ReadinessControlResult(
        ReadinessControlId controlId,
        ReadinessGateGroup group,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc)
    {
        ControlId = controlId;
        Group = group;
        Status = status;
        Evidence = evidence;
        ReasonCode = reasonCode;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Identidade estável do controle.</summary>
    public ReadinessControlId ControlId { get; }

    /// <summary>Grupo de gate a que este controle pertence.</summary>
    public ReadinessGateGroup Group { get; }

    /// <summary>Desfecho resolvido — nunca <see cref="ReadinessControlStatus.Pass"/> sem evidência real (invariante aplicado pelos factories).</summary>
    public ReadinessControlStatus Status { get; }

    /// <summary>Referência opaca à evidência usada para resolver <see cref="Status"/>.</summary>
    public ReadinessEvidenceReference Evidence { get; }

    /// <summary>Código curto e sanitizado explicando o desfecho (ex.: <c>"PENTEST_NOT_PERFORMED"</c>) — nunca segredo/PII.</summary>
    public string ReasonCode { get; }

    /// <summary>Instante em que a evidência subjacente foi observada/produzida (não o instante da composição do snapshot).</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Cria um resultado explícito para um controle — a única forma de obter <see cref="ReadinessControlStatus.Pass"/> é fornecer evidência real (<paramref name="evidence"/> diferente de <see cref="ReadinessEvidenceReference.None"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="reasonCode"/> tem aparência de segredo/PII, ou <paramref name="status"/> é <see cref="ReadinessControlStatus.Pass"/> sem evidência real.</exception>
    public static ReadinessControlResult Create(
        ReadinessControlId controlId,
        ReadinessGateGroup group,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (status == ReadinessControlStatus.Pass && evidence.Kind == ReadinessEvidenceKind.None)
        {
            throw new ArgumentException(
                "Pass exige evidência real (Kind != None) — não é possível declarar conformidade sem evidência.",
                nameof(status));
        }

        var normalizedReasonCode = EvidenceText.RequireSafeOptional(
            reasonCode, nameof(reasonCode), MaxReasonCodeLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p));

        return new ReadinessControlResult(controlId, group, status, evidence, normalizedReasonCode, observedAtUtc);
    }

    /// <summary>Resultado canônico fail-closed para um controle cuja evidência ainda não foi produzida.</summary>
    public static ReadinessControlResult NotMeasured(ReadinessControlId controlId, ReadinessGateGroup group, string reasonCode, DateTimeOffset observedAtUtc) =>
        Create(controlId, group, ReadinessControlStatus.NotMeasured, ReadinessEvidenceReference.None, reasonCode, observedAtUtc);
}
