namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Função PURA e determinística de agregação do Production Readiness Review (AB-I8-001, escopo obrigatório
/// item 1): recebe os desfechos JÁ RESOLVIDOS de cada controle (a Application layer resolve evidência a
/// partir dos stores canônicos ANTES de chamar este tipo) e deriva o desfecho agregado. Nunca faz I/O, nunca
/// chama Purview/Graph/EXO/AzCopy/host real (STOP-THE-LINE), nunca fabrica <see cref="ReadinessControlStatus.Pass"/>
/// para um controle ausente do dicionário fornecido.
/// <para>
/// Fail-closed por construção: iterar <see cref="ReadinessControlCatalog.AllControls"/> (nunca a chave do
/// dicionário fornecido) garante que um controle obrigatório nunca "some" silenciosamente do relatório só
/// porque o chamador esqueceu de resolvê-lo — a ausência vira, ela própria, um controle
/// <see cref="ReadinessControlStatus.NotMeasured"/> sintetizado, que bloqueia <c>ReadyForCanary</c> como
/// qualquer outro controle não-Pass.
/// </para>
/// </summary>
public static class ProductionReadinessGateEvaluator
{
    private const string MissingEvidenceReasonCode = "CONTROL_EVIDENCE_MISSING";

    /// <summary>
    /// Agrega os desfechos resolvidos de <paramref name="resolvedResults"/> contra o catálogo fixo,
    /// determinando <see cref="ProductionReadinessOutcome.ReadyForCanary"/> se e somente se TODO controle do
    /// catálogo está <see cref="ReadinessControlStatus.Pass"/>.
    /// </summary>
    public static ProductionReadinessEvaluation Evaluate(
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> resolvedResults,
        DateTimeOffset observedAtUtcForMissing)
    {
        ArgumentNullException.ThrowIfNull(resolvedResults);

        var orderedResults = new List<ReadinessControlResult>(ReadinessControlCatalog.AllControls.Count);
        var blockers = new List<ProductionReadinessBlocker>();

        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            var result = ResolveOrSynthesizeMissing(resolvedResults, definition, observedAtUtcForMissing);
            orderedResults.Add(result);

            if (result.Status != ReadinessControlStatus.Pass)
            {
                blockers.Add(new ProductionReadinessBlocker(definition.Id, definition.Group, result.Status, result.ReasonCode));
            }
        }

        // Defesa em profundidade (mesmo padrão "impossível por construção" de PenTestReadinessStatus):
        // ReadyForCanary SOMENTE quando não há absolutamente nenhum blocker — nunca um cálculo alternativo
        // que possa divergir desta condição.
        var outcome = blockers.Count == 0 ? ProductionReadinessOutcome.ReadyForCanary : ProductionReadinessOutcome.NotReady;

        return new ProductionReadinessEvaluation(outcome, orderedResults, blockers);
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

        // Defesa contra um chamador que (por erro) resolveu o controle errado sob a chave certa, ou tentou
        // classificar um controle SystemDerived como se tivesse vindo de atestação manual — nunca confiamos
        // cegamente no Group/Kind do valor fornecido; qualquer incoerência falha fechado como NotMeasured
        // (nunca lança uma exceção que interromperia a agregação inteira dos demais controles, e nunca
        // aceita silenciosamente um valor incoerente como se fosse válido).
        if (provided.Group != definition.Group || provided.ControlId != definition.Id)
        {
            return ReadinessControlResult.NotMeasured(definition.Id, definition.Group, "CONTROL_RESULT_MISMATCH", observedAtUtcForMissing);
        }

        return provided;
    }
}
