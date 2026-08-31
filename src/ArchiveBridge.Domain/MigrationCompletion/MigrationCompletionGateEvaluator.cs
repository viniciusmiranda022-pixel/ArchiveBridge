using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Função PURA e determinística de agregação do gate de encerramento de migração (AB-I8-010, escopo
/// obrigatório itens 7-8): recebe os desfechos JÁ RESOLVIDOS de cada critério do §49 (a Application layer
/// resolve evidência a partir dos stores canônicos/atestações ANTES de chamar este tipo) e deriva o desfecho
/// agregado. Nunca faz I/O, nunca chama Purview/Graph/EXO/AzCopy/host real, nunca executa decommission/
/// exclusão destrutiva (STOP-THE-LINE), nunca fabrica <see cref="ReadinessControlStatus.Pass"/> para um
/// critério ausente do dicionário fornecido.
/// <para>
/// Fail-closed por construção: iterar <see cref="MigrationCompletionCriterionCatalog.AllCriteria"/> (nunca a
/// chave do dicionário fornecido) garante que um critério obrigatório nunca "some" silenciosamente — a
/// ausência vira, ela própria, um critério <see cref="ReadinessControlStatus.NotMeasured"/> sintetizado, que
/// bloqueia <c>Eligible</c> como qualquer outro critério não-Pass (escopo obrigatório item 8: "Completed é
/// impossível enquanto qualquer critério obrigatório estiver ausente, inválido, stale, Unknown, Blocked,
/// NotPerformed, Pending ou Fail").
/// </para>
/// </summary>
public static class MigrationCompletionGateEvaluator
{
    private const string MissingEvidenceReasonCode = "CRITERION_EVIDENCE_MISSING";

    /// <summary>
    /// Agrega os desfechos resolvidos de <paramref name="resolvedResults"/> contra o catálogo fixo dos onze
    /// critérios, determinando <see cref="MigrationCompletionOutcome.Eligible"/> se e somente se TODOS os
    /// critérios do catálogo estão <see cref="ReadinessControlStatus.Pass"/>.
    /// </summary>
    public static MigrationCompletionEvaluation Evaluate(
        IReadOnlyDictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> resolvedResults,
        DateTimeOffset observedAtUtcForMissing)
    {
        ArgumentNullException.ThrowIfNull(resolvedResults);

        var orderedResults = new List<MigrationCompletionCriterionResult>(MigrationCompletionCriterionCatalog.AllCriteria.Count);
        var blockers = new List<MigrationCompletionBlocker>();

        foreach (var definition in MigrationCompletionCriterionCatalog.AllCriteria)
        {
            var result = ResolveOrSynthesizeMissing(resolvedResults, definition, observedAtUtcForMissing);
            orderedResults.Add(result);

            if (result.Status != ReadinessControlStatus.Pass)
            {
                blockers.Add(new MigrationCompletionBlocker(definition.Id, result.Status, result.ReasonCode));
            }
        }

        // Defesa em profundidade (mesmo padrão "impossível por construção" de ProductionReadinessGateEvaluator/
        // CanaryGateEvaluator/GoLiveGateEvaluator): Eligible SOMENTE quando não há absolutamente nenhum
        // blocker — nunca um cálculo alternativo que possa divergir desta condição.
        var outcome = blockers.Count == 0 ? MigrationCompletionOutcome.Eligible : MigrationCompletionOutcome.Blocked;

        return new MigrationCompletionEvaluation(outcome, orderedResults, blockers);
    }

    private static MigrationCompletionCriterionResult ResolveOrSynthesizeMissing(
        IReadOnlyDictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> resolvedResults,
        MigrationCompletionCriterionDefinition definition,
        DateTimeOffset observedAtUtcForMissing)
    {
        if (!resolvedResults.TryGetValue(definition.Id, out var provided))
        {
            return MigrationCompletionCriterionResult.NotMeasured(definition.Id, MissingEvidenceReasonCode, observedAtUtcForMissing);
        }

        // Defesa contra um chamador que (por erro) resolveu o critério errado sob a chave certa — nunca
        // confiamos cegamente no CriterionId do valor fornecido; qualquer incoerência falha fechado como
        // NotMeasured (mesmo princípio dos demais avaliadores deste repositório).
        if (provided.CriterionId != definition.Id)
        {
            return MigrationCompletionCriterionResult.NotMeasured(definition.Id, "CRITERION_RESULT_MISMATCH", observedAtUtcForMissing);
        }

        return provided;
    }
}
