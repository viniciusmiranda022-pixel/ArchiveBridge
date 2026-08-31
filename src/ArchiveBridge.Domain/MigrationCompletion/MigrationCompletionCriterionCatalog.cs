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
/// critério <see cref="MigrationCompletionCriterionEvidenceSource.SystemDerived"/>/
/// <see cref="MigrationCompletionCriterionEvidenceSource.EvidenceDerived"/> como atestável por operador.
/// </para>
/// <para>
/// AB-I8-011 (correção obrigatória sobre AB-I8-010): a revisão independente do HEAD original encontrou um
/// blocker de trust boundary — critérios tecnicamente/objetivamente verificáveis estavam classificados como
/// genericamente <c>Attested</c>, permitindo que uma alegação humana isolada substituísse a ausência de um
/// store canônico. Cada critério abaixo foi reavaliado individualmente contra os stores/cadeias canônicas já
/// existentes neste repositório; SOMENTE os critérios cuja verdade é GENUINAMENTE processual/de decisão humana
/// permanecem <see cref="MigrationCompletionCriterionEvidenceSource.HumanApproval"/>. Os critérios cuja verdade
/// é técnica/objetiva mas para os quais este repositório ainda não expõe um store canônico SUFICIENTE são
/// <see cref="MigrationCompletionCriterionEvidenceSource.EvidenceDerived"/> — permanentemente bloqueantes
/// (nunca satisfeitos por atestação) até que um store real seja implementado em um slice futuro.
/// </para>
/// </summary>
public static class MigrationCompletionCriterionCatalog
{
    /// <summary>Versão do catálogo — gravada em toda avaliação nova, nunca reescrita.</summary>
    public const string CurrentCatalogVersion = "archivebridge.migration-completion.criterion-catalog.v1";

    // §49 bullet 1 — escopo e policy version assinados. Genuinamente processual (assinatura/sign-off de
    // escopo/policy version não é um fato observável por nenhum store técnico deste repositório) — HumanApproval
    // (mesmo princípio de controles processuais sem store dedicado em ReadinessControlCatalog, ex.
    // ARCH.ADR_APPROVED). Revisado por AB-I8-011 item 4: elegível a permanecer humano/evidence-derived.
    private static readonly MigrationCompletionCriterionDefinition ScopeAndPolicySigned = Define(
        "COMPLETION.SCOPE_AND_POLICY_SIGNED", MigrationCompletionCriterionEvidenceSource.HumanApproval,
        "Escopo e policy version estão assinados (§49).");

    // §49 bullet 2 — todas as fontes têm disposition. AB-I8-011 item 1/3: verdade TÉCNICA/objetiva (se cada
    // fonte foi disposta), não uma opinião humana — mas este repositório não possui hoje nenhum
    // inventário/disposition canônico de "fontes" (SourceArchive é um skeleton sem estado; o único conceito de
    // "Disposition" existente, ReconciliationExceptionDisposition, cobre exceções de reconciliação por item, não
    // a disposition de uma fonte completa). Sem superfície canônica suficiente para resolver "TODAS as fontes"
    // — EvidenceDerived, permanentemente NotMeasured (nunca Pass por atestação) até existir um store real.
    private static readonly MigrationCompletionCriterionDefinition SourceDispositionComplete = Define(
        "COMPLETION.SOURCE_DISPOSITION_COMPLETE", MigrationCompletionCriterionEvidenceSource.EvidenceDerived,
        "Todas as fontes têm disposition (§49) — sem store canônico de inventário/disposition de fontes neste " +
        "repositório; permanece NotMeasured até existir fonte de evidência verificável (AB-I8-011).");

    // §49 bullet 3 — todas as parts estão importadas, filtradas por policy ou em exceção aprovada. AB-I8-011
    // item 1/3: verdade TÉCNICA/objetiva — mas este repositório não expõe um resolver COMPLETO: só existe
    // cobertura canônica parcial para o sub-caso de exceção de reconciliação (ReconciliationExceptionDisposition),
    // nada para "importada" ou "filtrada por política" como estado agregado por parte. Um resolver parcial
    // arriscaria produzir confiança falsa (Pass sem realmente saber que TODAS as parts foram dispostas) — por
    // isso EvidenceDerived, permanentemente NotMeasured até existir um resolver técnico completo.
    private static readonly MigrationCompletionCriterionDefinition PartsDispositionComplete = Define(
        "COMPLETION.PARTS_DISPOSITION_COMPLETE", MigrationCompletionCriterionEvidenceSource.EvidenceDerived,
        "Todas as parts estão importadas, filtradas por política ou em exceção aprovada (§49) — sem resolver " +
        "técnico COMPLETO neste repositório; permanece NotMeasured até existir fonte de evidência verificável " +
        "e completa (AB-I8-011).");

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

    // §49 bullet 6 — holds/retention foram revisados pelo owner. AB-I8-011 item 5: "revisado pelo owner" é, por
    // natureza, um ato de decisão humana (não um fato observável automaticamente) — este repositório possui
    // apenas um sinal técnico PARCIAL e diferente (MailboxPrecheckSnapshot.LitigationHoldEnabled/
    // RetentionHoldEnabled observa "há hold ativo agora", não "o owner revisou e aprovou holds/retention para
    // esta migração"), insuficiente para substituir a revisão do owner. Permanece HumanApproval.
    private static readonly MigrationCompletionCriterionDefinition HoldsRetentionReviewed = Define(
        "COMPLETION.HOLDS_RETENTION_REVIEWED", MigrationCompletionCriterionEvidenceSource.HumanApproval,
        "Holds/retention foram revisados pelo owner (§49).");

    // §49 bullet 7 — usuários e inativos foram tratados conforme mapeamento. AB-I8-011 item 5: nenhum store de
    // mapeamento usuário-a-tratamento/inativo existe neste repositório (MappingDocument/MappingCsv modelam
    // mapeamento de pasta/caixa de destino, não disposition de usuário/inativo) — "tratados conforme
    // mapeamento" permanece uma confirmação genuinamente processual do operador. Permanece HumanApproval.
    private static readonly MigrationCompletionCriterionDefinition UsersInactiveHandled = Define(
        "COMPLETION.USERS_INACTIVE_HANDLED", MigrationCompletionCriterionEvidenceSource.HumanApproval,
        "Usuários e inativos foram tratados conforme mapeamento (§49).");

    // §49 bullet 8 — pacote de evidência foi assinado e publicado WORM. AB-I8-011 item 1/3: verdade
    // TÉCNICA/objetiva (foi ou não publicado em WORM com assinatura real) — uma evidence ref humana NÃO prova
    // publicação WORM real. A cadeia de custódia existente (CustodyEvent/IEvidenceLedger) é explicitamente um
    // skeleton ("assinatura de evidência e WORM entram por slice posterior — ADR pendente") sem nenhuma
    // implementação de Infrastructure e sem verificador de assinatura/imutabilidade. Sem essa autoridade real —
    // EvidenceDerived, permanentemente NotMeasured até existir o mecanismo real de assinatura/publicação WORM.
    private static readonly MigrationCompletionCriterionDefinition EvidencePackagePublishedWorm = Define(
        "COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM", MigrationCompletionCriterionEvidenceSource.EvidenceDerived,
        "Pacote de evidência foi assinado e publicado em WORM (§49) — sem mecanismo real de assinatura/" +
        "publicação WORM neste repositório (CustodyEvent/IEvidenceLedger é skeleton, ADR pendente); permanece " +
        "NotMeasured até existir fonte de evidência verificável (AB-I8-011).");

    // §49 bullet 9 — janela de rollback e decommission foram definidas. AB-I8-011 item 4: definicional/
    // procedural (a existência de uma janela DEFINIDA é registrada pela decisão do operador, não observável por
    // nenhum store técnico) — elegível a permanecer humano/evidence-derived. Registro/verificação de DEFINIÇÃO
    // apenas — NUNCA execução de decommission/exclusão destrutiva (escopo obrigatório item 9, STOP-THE-LINE).
    private static readonly MigrationCompletionCriterionDefinition RollbackDecommissionWindowDefined = Define(
        "COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED", MigrationCompletionCriterionEvidenceSource.HumanApproval,
        "Janela de rollback e decommission foram DEFINIDAS (§49) — este critério registra/verifica apenas a " +
        "definição da janela; nunca dispara ou representa a execução de decommission/exclusão destrutiva.");

    // §49 bullet 10 — cliente aprovou relatório final. AB-I8-011 item 4: aprovação do cliente é, por natureza,
    // uma decisão humana externa — a atestação (com evidence reference real e ator/papel/correlação
    // server-side) É a evidência auditável exigida — ausência nunca vira aprovação implícita (escopo
    // obrigatório item 8). Permanece HumanApproval.
    private static readonly MigrationCompletionCriterionDefinition CustomerFinalApproval = Define(
        "COMPLETION.CUSTOMER_FINAL_APPROVAL", MigrationCompletionCriterionEvidenceSource.HumanApproval,
        "Cliente aprovou o relatório final (§49) — exige evidência auditável explícita; ausência nunca é aprovação implícita.");

    // §49 bullet 11 — nenhuma credencial temporária permanece ativa. AB-I8-011 item 1/3: condição TÉCNICA
    // NEGATIVA objetiva ("nenhuma" credencial ativa) — uma alegação humana isolada nunca prova uma ausência
    // técnica. Este repositório possui apenas registries PARCIAIS e escopados por credencial/onda individual
    // (IPurviewSasUploadHandleStore/SasHandleState só cobre SAS de upload Purview, por onda;
    // IEnrollmentTokenStore/EnrollmentTokenStatus só cobre token de enrollment do EV connector), sem uma
    // consulta agregada "qualquer credencial temporária ativa para tenant/projeto X" através de todos os tipos e
    // ondas. Sem essa consulta agregada — EvidenceDerived, permanentemente NotMeasured (nunca Pass por
    // atestação, nunca presumido ausente por omissão) até existir um registry canônico agregado.
    private static readonly MigrationCompletionCriterionDefinition NoActiveTemporaryCredential = Define(
        "COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL", MigrationCompletionCriterionEvidenceSource.EvidenceDerived,
        "Nenhuma credencial temporária permanece ativa (§49) — sem registry canônico agregado de credenciais " +
        "temporárias neste repositório; permanece NotMeasured até existir fonte de evidência verificável (AB-I8-011).");

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
