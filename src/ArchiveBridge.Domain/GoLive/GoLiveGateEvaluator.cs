using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Domain.GoLive;

/// <summary>
/// Função PURA e determinística de agregação da decisão de go-live (AB-I8-010, escopo obrigatório itens 2-4):
/// recebe o desfecho JÁ RESOLVIDO do canário canônico vinculado, a identidade do Production Readiness Review
/// canônico VIGENTE (para detecção de drift contra o vinculado pelo plano de canário) e os desfechos JÁ
/// RESOLVIDOS, FRESCOS no instante desta decisão, de cada controle operacional/M365 do catálogo do Passo 1 — e
/// deriva o desfecho agregado. Nunca faz I/O, nunca chama Purview/Graph/EXO/AzCopy/host real (STOP-THE-LINE),
/// nunca fabrica <see cref="ReadinessControlStatus.Pass"/> para um controle ausente do dicionário fornecido.
/// <para>
/// Fail-closed por construção: iterar o subconjunto Operations/Microsoft365 de
/// <see cref="ReadinessControlCatalog.AllControls"/> (nunca a chave do dicionário fornecido) garante que um
/// controle obrigatório nunca "some" silenciosamente — a ausência vira, ela própria, um controle
/// <see cref="ReadinessControlStatus.NotMeasured"/> sintetizado, que bloqueia <c>GoLiveAuthorized</c> como
/// qualquer outro controle não-Pass.
/// </para>
/// </summary>
public static class GoLiveGateEvaluator
{
    private const string MissingEvidenceReasonCode = "OPERATIONAL_CONTROL_EVIDENCE_MISSING";

    /// <summary>Os controles operacionais/M365 revalidados FRESCOS por este gate (§47.4/§47.5) — subconjunto fixo do catálogo do Passo 1, na ordem declarada do catálogo.</summary>
    public static readonly IReadOnlyList<ReadinessControlDefinition> OperationalControls =
    [
        .. ReadinessControlCatalog.AllControls.Where(
            definition => definition.Group is ReadinessGateGroup.Operations or ReadinessGateGroup.Microsoft365),
    ];

    /// <summary>
    /// Agrega o desfecho do canário, a identidade do review vigente contra a vinculada pelo plano de canário, e
    /// os controles operacionais resolvidos, determinando <see cref="GoLiveOutcome.GoLiveAuthorized"/> se e
    /// somente se TODOS os gates estiverem satisfeitos.
    /// </summary>
    public static GoLiveEvaluation Evaluate(
        CanaryOutcome canaryOutcome,
        int boundReadinessReviewVersion,
        Sha256Hash boundReadinessReviewFingerprint,
        int? currentReadinessReviewVersion,
        Sha256Hash? currentReadinessReviewFingerprint,
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> operationalResolvedResults,
        DateTimeOffset observedAtUtcForMissing)
    {
        ArgumentNullException.ThrowIfNull(operationalResolvedResults);

        var blockers = new List<GoLiveBlocker>();

        if (canaryOutcome != CanaryOutcome.CanaryPassed)
        {
            blockers.Add(new GoLiveBlocker(GoLiveBlocker.CanaryNotPassedCode, $"CANARY_OUTCOME_{canaryOutcome}"));
        }

        var readinessDrifted = currentReadinessReviewVersion is null
            || currentReadinessReviewFingerprint is null
            || currentReadinessReviewVersion.Value != boundReadinessReviewVersion
            || !string.Equals(currentReadinessReviewFingerprint.Value.Value, boundReadinessReviewFingerprint.Value, StringComparison.Ordinal);
        if (readinessDrifted)
        {
            blockers.Add(new GoLiveBlocker(GoLiveBlocker.ReadinessReviewDriftCode, "READINESS_REVIEW_NO_LONGER_MATCHES_CANARY"));
        }

        var orderedOperationalResults = new List<ReadinessControlResult>(OperationalControls.Count);
        foreach (var definition in OperationalControls)
        {
            var result = ResolveOrSynthesizeMissing(operationalResolvedResults, definition, observedAtUtcForMissing);
            orderedOperationalResults.Add(result);

            if (result.Status != ReadinessControlStatus.Pass)
            {
                blockers.Add(new GoLiveBlocker($"{GoLiveBlocker.OperationalControlNotPassCode}:{definition.Id.Value}", result.ReasonCode));
            }
        }

        // Defesa em profundidade (mesmo padrão "impossível por construção" de CanaryGateEvaluator/
        // ProductionReadinessGateEvaluator): GoLiveAuthorized SOMENTE quando não há absolutamente nenhum
        // blocker — nunca um cálculo alternativo que possa divergir desta condição.
        var outcome = blockers.Count == 0 ? GoLiveOutcome.GoLiveAuthorized : GoLiveOutcome.Blocked;

        return new GoLiveEvaluation(outcome, orderedOperationalResults, blockers);
    }

    private static ReadinessControlResult ResolveOrSynthesizeMissing(
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> resolvedResults,
        ReadinessControlDefinition definition,
        DateTimeOffset observedAtUtcForMissing)
    {
        if (!resolvedResults.TryGetValue(definition.Id, out var provided))
        {
            return ReadinessControlResult.NotMeasured(definition.Id, definition.Group, MissingEvidenceReasonCode, observedAtUtcForMissing);
        }

        // Defesa contra um chamador que (por erro) resolveu o controle errado sob a chave certa, ou incluiu um
        // controle fora do subconjunto Operations/Microsoft365 — nunca confiamos cegamente no Group/Id do
        // valor fornecido; qualquer incoerência falha fechado como NotMeasured (mesmo princípio de
        // ProductionReadinessGateEvaluator.ResolveOrSynthesizeMissing).
        if (provided.Group != definition.Group || provided.ControlId != definition.Id)
        {
            return ReadinessControlResult.NotMeasured(definition.Id, definition.Group, "OPERATIONAL_CONTROL_RESULT_MISMATCH", observedAtUtcForMissing);
        }

        return provided;
    }
}
