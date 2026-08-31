using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Desfecho RESOLVIDO de UM cenário do canário, já composto pelo chamador (Application layer) a partir de
/// evidência canônica ou de atestação de operador — este tipo NUNCA resolve evidência por si só, apenas
/// representa um resultado já decidido (mesmo princípio de
/// <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlResult"/>).
/// </summary>
public sealed record CanaryScenarioResult
{
    private const int MaxReasonCodeLength = 200;

    private CanaryScenarioResult(
        CanaryScenarioId scenarioId,
        CanaryScenarioStatus status,
        CanaryEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc)
    {
        ScenarioId = scenarioId;
        Status = status;
        Evidence = evidence;
        ReasonCode = reasonCode;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Identidade estável do cenário.</summary>
    public CanaryScenarioId ScenarioId { get; }

    /// <summary>Desfecho resolvido — nunca <see cref="CanaryScenarioStatus.Pass"/> sem evidência real (invariante aplicado pelo factory).</summary>
    public CanaryScenarioStatus Status { get; }

    /// <summary>Referência opaca à evidência usada para resolver <see cref="Status"/> — preserva provenance (Kind) e correlação de auditoria via o localizador.</summary>
    public CanaryEvidenceReference Evidence { get; }

    /// <summary>Código curto e sanitizado explicando o desfecho — nunca segredo/PII.</summary>
    public string ReasonCode { get; }

    /// <summary>Instante em que a evidência subjacente foi observada/produzida (source/timestamp de auditoria, escopo obrigatório item 6).</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Cria um resultado explícito para um cenário — a única forma de obter <see cref="CanaryScenarioStatus.Pass"/> é fornecer evidência real (<paramref name="evidence"/> diferente de <see cref="CanaryEvidenceReference.None"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="reasonCode"/> tem aparência de segredo/PII, ou <paramref name="status"/> é <see cref="CanaryScenarioStatus.Pass"/> sem evidência real.</exception>
    public static CanaryScenarioResult Create(
        CanaryScenarioId scenarioId,
        CanaryScenarioStatus status,
        CanaryEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (status == CanaryScenarioStatus.Pass && evidence.Kind == CanaryEvidenceKind.None)
        {
            throw new ArgumentException(
                "Pass exige evidência real (Kind != None) — não é possível declarar sucesso sem evidência.",
                nameof(status));
        }

        var normalizedReasonCode = EvidenceText.RequireSafeOptional(
            reasonCode, nameof(reasonCode), MaxReasonCodeLength, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p));

        return new CanaryScenarioResult(scenarioId, status, evidence, normalizedReasonCode, observedAtUtc);
    }

    /// <summary>Resultado canônico fail-closed para um cenário cuja evidência ainda não foi produzida.</summary>
    public static CanaryScenarioResult Pending(CanaryScenarioId scenarioId, string reasonCode, DateTimeOffset observedAtUtc) =>
        Create(scenarioId, CanaryScenarioStatus.Pending, CanaryEvidenceReference.None, reasonCode, observedAtUtc);

    /// <summary>
    /// Impressão digital determinística do CONTEÚDO deste resultado (AB-I8-004 escopo obrigatório item 6:
    /// "replay idêntico deve convergir") — chave de convergência idempotente usada pela camada de
    /// persistência: a MESMA combinação de status/evidência/motivo/instante-observado converge para a MESMA
    /// versão de resultado, nunca duplica a linha; qualquer mudança real produz uma versão nova. NUNCA cobre
    /// versão de resultado/ator/correlação/instante de submissão (mesmo princípio de
    /// <see cref="ArchiveBridge.Domain.ProductionReadiness.ProductionReadinessReviewSnapshot.ComputeReviewFingerprint"/>).
    /// </summary>
    public static Sha256Hash ComputeContentFingerprint(
        CanaryScenarioId scenarioId, CanaryScenarioStatus status, CanaryEvidenceReference evidence, string reasonCode, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return DeterministicHash.Compute(
        [
            "archivebridge.canary.scenario-result-content.v1",
            scenarioId.Value,
            ((int)status).ToString(CultureInfo.InvariantCulture),
            ((int)evidence.Kind).ToString(CultureInfo.InvariantCulture),
            evidence.Fingerprint.Value,
            evidence.Locator,
            reasonCode,
            observedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);
    }

    /// <summary>
    /// Hash determinístico de TODOS os campos persistidos de UMA linha de resultado (header + auditoria) —
    /// recomputado e validado fail-closed pela camada de persistência em toda leitura (escopo obrigatório
    /// item 12: "tampering de status, fingerprints, evidence refs ou resultados deve falhar fechado"). Uso
    /// exclusivo da camada de persistência — a Application layer nunca constrói nem inspeciona este valor.
    /// </summary>
    public static Sha256Hash ComputeRecordHash(
        Guid tenantId,
        Guid projectId,
        int planVersion,
        CanaryScenarioId scenarioId,
        int resultVersion,
        CanaryScenarioStatus status,
        CanaryEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        string schemaVersion,
        Sha256Hash contentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return DeterministicHash.Compute(
        [
            nameof(CanaryScenarioResult),
            schemaVersion,
            tenantId.ToString("N"),
            projectId.ToString("N"),
            planVersion.ToString(CultureInfo.InvariantCulture),
            scenarioId.Value,
            resultVersion.ToString(CultureInfo.InvariantCulture),
            ((int)status).ToString(CultureInfo.InvariantCulture),
            ((int)evidence.Kind).ToString(CultureInfo.InvariantCulture),
            evidence.Fingerprint.Value,
            evidence.Locator,
            reasonCode,
            observedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            submittedBy,
            submittedByRole,
            correlation.Value.ToString("N"),
            recordedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            contentFingerprint.Value,
        ]);
    }
}
