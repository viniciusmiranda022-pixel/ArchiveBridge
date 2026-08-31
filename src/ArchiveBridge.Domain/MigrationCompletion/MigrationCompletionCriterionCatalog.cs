namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Catálogo FIXO e versionado dos ONZE critérios obrigatórios de encerramento de uma migração (AB-I8-010,
/// runbook §49) — a ÚNICA fonte de verdade de quais critérios existem e como podem ser resolvidos
/// (<see cref="MigrationCompletionCriterionEvidenceSource"/>). Cada entrada corresponde 1:1 a um bullet
/// literal do §49 (fonte de autoridade citada no work order) — nenhum critério aspiracional adicional. TODOS
/// os critérios são obrigatórios (o runbook não distingue itens opcionais).
/// <para>
/// Nenhum chamador informa a lista de critérios ou sua classe de evidência: tudo é sempre derivado daqui
/// (mesmo princípio de <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlCatalog"/>), de
/// forma que nenhuma identidade/papel possa "inventar" um critério novo que sempre passa, nem reclassificar um
/// critério <see cref="MigrationCompletionCriterionEvidenceSource.SystemDerived"/> como atestável por operador.
/// </para>
/// </summary>
public static class MigrationCompletionCriterionCatalog
{
    /// <summary>Versão do catálogo — gravada em toda avaliação nova, nunca reescrita.</summary>
    public const string CurrentCatalogVersion = "archivebridge.migration-completion.criterion-catalog.v1";

    // §49 bullet 1 — escopo e policy version assinados. Nenhum store dedicado de "assinatura de escopo/policy"
    // existe hoje neste repositório — Attested (mesmo princípio de controles processuais sem store dedicado em
    // ReadinessControlCatalog, ex. ARCH.ADR_APPROVED).
    private static readonly MigrationCompletionCriterionDefinition ScopeAndPolicySigned = Define(
        "COMPLETION.SCOPE_AND_POLICY_SIGNED", MigrationCompletionCriterionEvidenceSource.Attested,
        "Escopo e policy version estão assinados (§49).");

    // §49 bullet 2 — todas as fontes possuem disposition.
    private static readonly MigrationCompletionCriterionDefinition SourceDispositionComplete = Define(
        "COMPLETION.SOURCE_DISPOSITION_COMPLETE", MigrationCompletionCriterionEvidenceSource.Attested,
        "Todas as fontes têm disposition (§49).");

    // §49 bullet 3 — todas as parts estão importadas, filtradas por policy ou em exceção aprovada.
    private static readonly MigrationCompletionCriterionDefinition PartsDispositionComplete = Define(
        "COMPLETION.PARTS_DISPOSITION_COMPLETE", MigrationCompletionCriterionEvidenceSource.Attested,
        "Todas as parts estão importadas, filtradas por política ou em exceção aprovada (§49).");

    // §49 bullet 4 — resultados do provider foram coletados. Fonte canônica real já existente
    // (IPurviewServiceResultReportStore, I6) — SystemDerived.
    private static readonly MigrationCompletionCriterionDefinition ProviderResultsCollected = Define(
        "COMPLETION.PROVIDER_RESULTS_COLLECTED", MigrationCompletionCriterionEvidenceSource.SystemDerived,
        "Resultados do provider foram coletados (§49) — resolvido a partir do validation report/service result " +
        "mais recente já importado (IPurviewServiceResultReportStore, I6) da onda/plano de import job informados.");

    // §49 bullet 5 — reconciliação fechou. Fonte canônica real já existente (IReconciliationCertificateStore, I6).
    private static readonly MigrationCompletionCriterionDefinition ReconciliationClosed = Define(
        "COMPLETION.RECONCILIATION_CLOSED", MigrationCompletionCriterionEvidenceSource.SystemDerived,
        "Reconciliação fechou (§49) — resolvido a partir do reconciliation certificate canônico e vigente " +
        "(IReconciliationCertificateStore, I6) da onda/plano de import job informados; INCONCLUSIVE/FAIL/" +
        "DuplicateRisk/evidência incompleta nunca é Pass.");

    // §49 bullet 6 — holds/retention foram revisados pelo owner.
    private static readonly MigrationCompletionCriterionDefinition HoldsRetentionReviewed = Define(
        "COMPLETION.HOLDS_RETENTION_REVIEWED", MigrationCompletionCriterionEvidenceSource.Attested,
        "Holds/retention foram revisados pelo owner (§49).");

    // §49 bullet 7 — usuários e inativos foram tratados conforme mapeamento.
    private static readonly MigrationCompletionCriterionDefinition UsersInactiveHandled = Define(
        "COMPLETION.USERS_INACTIVE_HANDLED", MigrationCompletionCriterionEvidenceSource.Attested,
        "Usuários e inativos foram tratados conforme mapeamento (§49).");

    // §49 bullet 8 — pacote de evidência foi assinado e publicado WORM.
    private static readonly MigrationCompletionCriterionDefinition EvidencePackagePublishedWorm = Define(
        "COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM", MigrationCompletionCriterionEvidenceSource.Attested,
        "Pacote de evidência foi assinado e publicado em WORM (§49).");

    // §49 bullet 9 — janela de rollback e decommission foram definidas. Registro/verificação de DEFINIÇÃO
    // apenas — NUNCA execução de decommission/exclusão destrutiva (escopo obrigatório item 9, STOP-THE-LINE).
    private static readonly MigrationCompletionCriterionDefinition RollbackDecommissionWindowDefined = Define(
        "COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED", MigrationCompletionCriterionEvidenceSource.Attested,
        "Janela de rollback e decommission foram DEFINIDAS (§49) — este critério registra/verifica apenas a " +
        "definição da janela; nunca dispara ou representa a execução de decommission/exclusão destrutiva.");

    // §49 bullet 10 — cliente aprovou relatório final. A atestação (com evidence reference real e ator/papel/
    // correlação server-side) É a evidência auditável exigida — ausência nunca vira aprovação implícita
    // (escopo obrigatório item 8).
    private static readonly MigrationCompletionCriterionDefinition CustomerFinalApproval = Define(
        "COMPLETION.CUSTOMER_FINAL_APPROVAL", MigrationCompletionCriterionEvidenceSource.Attested,
        "Cliente aprovou o relatório final (§49) — exige evidência auditável explícita; ausência nunca é aprovação implícita.");

    // §49 bullet 11 — nenhuma credencial temporária permanece ativa.
    private static readonly MigrationCompletionCriterionDefinition NoActiveTemporaryCredential = Define(
        "COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL", MigrationCompletionCriterionEvidenceSource.Attested,
        "Nenhuma credencial temporária permanece ativa (§49) — ausência de atestação bloqueia (nunca presumido por omissão).");

    private static readonly MigrationCompletionCriterionDefinition[] Definitions =
    [
        ScopeAndPolicySigned,
        SourceDispositionComplete,
        PartsDispositionComplete,
        ProviderResultsCollected,
        ReconciliationClosed,
        HoldsRetentionReviewed,
        UsersInactiveHandled,
        EvidencePackagePublishedWorm,
        RollbackDecommissionWindowDefined,
        CustomerFinalApproval,
        NoActiveTemporaryCredential,
    ];

    private static readonly Dictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id);

    /// <summary>Todos os critérios do catálogo, na ordem declarada acima (determinística).</summary>
    public static IReadOnlyList<MigrationCompletionCriterionDefinition> AllCriteria { get; } = Definitions;

    /// <summary>A definição FIXA de um critério — nunca fornecida/alterável pelo chamador.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="criterionId"/> não pertence a este catálogo.</exception>
    public static MigrationCompletionCriterionDefinition Definition(MigrationCompletionCriterionId criterionId)
    {
        if (!ById.TryGetValue(criterionId, out var definition))
        {
            throw new ArgumentOutOfRangeException(nameof(criterionId), criterionId.Value, "Critério de encerramento desconhecido neste catálogo.");
        }

        return definition;
    }

    /// <summary>Verdadeiro quando <paramref name="criterionId"/> pertence a este catálogo.</summary>
    public static bool IsKnown(MigrationCompletionCriterionId criterionId) => ById.ContainsKey(criterionId);

    private static MigrationCompletionCriterionDefinition Define(
        string id, MigrationCompletionCriterionEvidenceSource evidenceSource, string description) =>
        new(new MigrationCompletionCriterionId(id), evidenceSource, description);
}
