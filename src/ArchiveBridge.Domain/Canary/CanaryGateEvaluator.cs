namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Função PURA e determinística de agregação do canário (AB-I8-004, escopo obrigatório item 4): recebe os
/// desfechos JÁ RESOLVIDOS de cada cenário (a Application layer resolve evidência a partir dos stores
/// canônicos/atestações de operador ANTES de chamar este tipo) e deriva o desfecho agregado. Nunca faz I/O,
/// nunca chama Purview/Graph/EXO/AzCopy/host real (STOP-THE-LINE), nunca fabrica
/// <see cref="CanaryScenarioStatus.Pass"/> para um cenário ausente do dicionário fornecido.
/// <para>
/// Fail-closed por construção: iterar <see cref="CanaryScenarioCatalog.AllScenarios"/> (nunca a chave do
/// dicionário fornecido) garante que um cenário obrigatório nunca "some" silenciosamente do relatório só
/// porque o chamador esqueceu de resolvê-lo — a ausência vira, ela própria, um cenário
/// <see cref="CanaryScenarioStatus.Pending"/> sintetizado, que bloqueia <c>CanaryPassed</c> como qualquer
/// outro cenário não-Pass.
/// </para>
/// </summary>
public static class CanaryGateEvaluator
{
    private const string MissingEvidenceReasonCode = "SCENARIO_EVIDENCE_MISSING";
    private const string MismatchReasonCode = "SCENARIO_RESULT_MISMATCH";

    /// <summary>
    /// Agrega os desfechos resolvidos de <paramref name="resolvedResults"/> contra o catálogo fixo,
    /// determinando <see cref="CanaryOutcome.CanaryPassed"/> se e somente se TODO cenário do catálogo está
    /// <see cref="CanaryScenarioStatus.Pass"/>.
    /// </summary>
    public static CanaryEvaluation Evaluate(
        IReadOnlyDictionary<CanaryScenarioId, CanaryScenarioResult> resolvedResults,
        DateTimeOffset observedAtUtcForMissing)
    {
        ArgumentNullException.ThrowIfNull(resolvedResults);

        var orderedResults = new List<CanaryScenarioResult>(CanaryScenarioCatalog.AllScenarios.Count);
        var blockers = new List<CanaryBlocker>();

        foreach (var definition in CanaryScenarioCatalog.AllScenarios)
        {
            var result = ResolveOrSynthesizeMissing(resolvedResults, definition, observedAtUtcForMissing);
            orderedResults.Add(result);

            if (result.Status != CanaryScenarioStatus.Pass)
            {
                blockers.Add(new CanaryBlocker(definition.Id, result.Status, result.ReasonCode));
            }
        }

        // Defesa em profundidade (mesmo padrão "impossível por construção" de ProductionReadinessGateEvaluator):
        // CanaryPassed SOMENTE quando não há absolutamente nenhum blocker — nunca um cálculo alternativo que
        // possa divergir desta condição.
        var outcome = blockers.Count == 0 ? CanaryOutcome.CanaryPassed : CanaryOutcome.NotPassed;

        return new CanaryEvaluation(outcome, orderedResults, blockers);
    }

    private static CanaryScenarioResult ResolveOrSynthesizeMissing(
        IReadOnlyDictionary<CanaryScenarioId, CanaryScenarioResult> resolvedResults,
        CanaryScenarioDefinition definition,
        DateTimeOffset observedAtUtcForMissing)
    {
        if (!resolvedResults.TryGetValue(definition.Id, out var provided))
        {
            return CanaryScenarioResult.Pending(definition.Id, MissingEvidenceReasonCode, observedAtUtcForMissing);
        }

        // Defesa contra um chamador que (por erro) resolveu o cenário errado sob a chave certa — nunca
        // confiamos cegamente no ScenarioId do valor fornecido; qualquer incoerência falha fechado como
        // Pending (nunca lança uma exceção que interromperia a agregação inteira dos demais cenários).
        if (provided.ScenarioId != definition.Id)
        {
            return CanaryScenarioResult.Pending(definition.Id, MismatchReasonCode, observedAtUtcForMissing);
        }

        return provided;
    }
}
