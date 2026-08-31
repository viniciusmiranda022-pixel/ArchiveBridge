namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Catálogo FIXO e versionado dos controles obrigatórios do Production Readiness Review (AB-I8-001) — a
/// ÚNICA fonte de verdade de quais controles existem, a qual grupo (§47.1-§47.5) pertencem e como podem ser
/// resolvidos (<see cref="ReadinessControlEvidenceSource"/>). Cada entrada corresponde 1:1 a um item literal
/// do texto do runbook §47 (fonte de autoridade citada no work order) — nenhum controle aspiracional
/// adicional, nenhuma paráfrase que mude o sentido do bullet original. TODOS os controles listados aqui são
/// obrigatórios nesta baseline (o runbook não distingue itens opcionais em §47).
/// <para>
/// Nenhum chamador informa a lista de controles, o grupo ou a classe de evidência: tudo é sempre derivado
/// daqui (mesmo princípio de <see cref="ArchiveBridge.Domain.Security.WorkerHardeningBaselineCatalog"/>), de
/// forma que nenhuma identidade/papel possa "inventar" um controle novo que sempre passa, nem reclassificar
/// um controle <see cref="ReadinessControlEvidenceSource.SystemDerived"/> como atestável manualmente.
/// </para>
/// </summary>
public static class ReadinessControlCatalog
{
    /// <summary>Versão do catálogo — gravada em todo snapshot novo, nunca reescrita.</summary>
    public const string CurrentCatalogVersion = "archivebridge.production-readiness.control-catalog.v1";

    private static readonly ReadinessControlDefinition[] Definitions =
    [
        // §47.1 — Gate de arquitetura. ADR/diagrama/ownership permanecem artefatos de processo/documentação
        // sem store dedicado — Attested (AB-I8-001 escopo item 2). CAPABILITY_MATRIX_CURRENT tem fonte
        // canônica real (AB-I8-002 blocker 1/catálogo revisado): ICapabilityEvidenceStore (I5) já persiste,
        // por rota Purview conhecida, o CapabilityStatus documentado pelo fornecedor com tamper-evidence
        // (EvidenceHash) — SystemDerived, resolvido via CapabilityEvidencePolicy.EnsureGeneralAvailability
        // (mesma política já usada pelo precheck gate real, AB-I5-001).
        Define("ARCH.ADR_APPROVED", ReadinessGateGroup.Architecture, ReadinessControlEvidenceSource.Attested,
            "ADRs aprovados (§47.1)."),
        Define("ARCH.DATA_FLOW_DIAGRAMS_CURRENT", ReadinessGateGroup.Architecture, ReadinessControlEvidenceSource.Attested,
            "Diagramas e data flow atualizados (§47.1)."),
        Define("ARCH.CAPABILITY_MATRIX_CURRENT", ReadinessGateGroup.Architecture, ReadinessControlEvidenceSource.SystemDerived,
            "Capability matrix atual (§47.1) — resolvido a partir de ICapabilityEvidenceStore (I5) para cada rota " +
            "Purview conhecida; Unknown/Unsupported/Preview/Contractual/ausente/stale nunca é promovido a Pass."),
        Define("ARCH.NO_PREVIEW_IN_GA_PATH", ReadinessGateGroup.Architecture, ReadinessControlEvidenceSource.Attested,
            "Nenhum preview no caminho GA (§47.1)."),
        Define("ARCH.SERVICE_OWNERSHIP_ASSIGNED", ReadinessGateGroup.Architecture, ReadinessControlEvidenceSource.Attested,
            "Owner de cada serviço (§47.1)."),

        // §47.2 — Gate de segurança. Threat model/secrets-scan/cross-tenant-suite ainda não têm store
        // dedicado nesta baseline (Attested); pen-test/SBOM-signatures/WDAC-Defender-patching/incident-
        // response JÁ possuem evidência canônica produzida pelo I7 (AB-I7-008) — SystemDerived, resolvidos
        // pelo agregador (AB-I8-001 escopo item 3).
        Define("SEC.THREAT_MODEL_CLOSED", ReadinessGateGroup.Security, ReadinessControlEvidenceSource.Attested,
            "Threat model fechado (§47.2)."),
        Define("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH", ReadinessGateGroup.Security, ReadinessControlEvidenceSource.SystemDerived,
            "Pen-test sem crítico/alto aberto (§47.2) — resolvido a partir do pen-test readiness bundle (AB-I7-008); " +
            "PenTestReadinessStatus não possui NENHUM caso Pass/concluído (bloqueio estrutural)."),
        Define("SEC.SECRETS_SCAN_CLEAN", ReadinessGateGroup.Security, ReadinessControlEvidenceSource.Attested,
            "Secrets scan limpo (§47.2)."),
        Define("SEC.SBOM_AND_SIGNATURES", ReadinessGateGroup.Security, ReadinessControlEvidenceSource.SystemDerived,
            "SBOM e assinaturas (§47.2) — resolvido a partir da build provenance aprovada (AB-I7-008) do build/digest revisado."),
        Define("SEC.WDAC_DEFENDER_PATCHING", ReadinessGateGroup.Security, ReadinessControlEvidenceSource.SystemDerived,
            "WDAC/Defender/patching (§47.2) — resolvido a partir da baseline de worker hardening + WDAC policy evidence (AB-I7-008)."),
        Define("SEC.CROSS_TENANT_TESTS", ReadinessGateGroup.Security, ReadinessControlEvidenceSource.Attested,
            "Cross-tenant tests (§47.2)."),
        Define("SEC.INCIDENT_RESPONSE_EXERCISED", ReadinessGateGroup.Security, ReadinessControlEvidenceSource.SystemDerived,
            "Incident response exercitado (§47.2) — resolvido a partir dos três drills sintéticos (AB-I7-008)."),

        // §47.3 — Gate de dados. Hashes/manifests/lineage e backup/restore JÁ possuem evidência de recovery
        // readiness (AB-I7-005) — SystemDerived. Privacy impact/retention/corpus-fidelity ainda Attested.
        Define("DATA.HASHES_MANIFESTS_LINEAGE_WORM", ReadinessGateGroup.Data, ReadinessControlEvidenceSource.SystemDerived,
            "Hashes, manifests, lineage e WORM (§47.3) — resolvido a partir do exercício de artifact/evidence recovery (AB-I7-005)."),
        Define("DATA.PRIVACY_IMPACT_ASSESSMENT", ReadinessGateGroup.Data, ReadinessControlEvidenceSource.Attested,
            "Privacy impact assessment (§47.3)."),
        Define("DATA.RETENTION_DELETION_DOCUMENTED", ReadinessGateGroup.Data, ReadinessControlEvidenceSource.Attested,
            "Retention/deletion documentada (§47.3)."),
        Define("DATA.BACKUP_RESTORE_TESTED", ReadinessGateGroup.Data, ReadinessControlEvidenceSource.SystemDerived,
            "Backup/restore testado (§47.3) — resolvido a partir do restore drill (AB-I7-005)."),
        Define("DATA.CORPUS_FIDELITY_REPORT_APPROVED", ReadinessGateGroup.Data, ReadinessControlEvidenceSource.Attested,
            "Corpus/fidelity report aprovado (§47.3)."),

        // §47.4 — Gate operacional. O bullet "RTO/RPO exercitados" é modelado como DOIS controles
        // (RTO e RPO) porque RecoveryObjective já os distingue como medições/alvos independentes
        // (ControlPlaneRto vs. ControlPlaneRpo/EvidenceLogicalRpo) — ambos SystemDerived a partir do
        // recovery readiness (AB-I7-005/AB-I7-007); RPO permanece estruturalmente Blocked/NotMeasured
        // nesta baseline (nenhum drill de failure-boundary real existe ainda). Os demais itens não têm
        // store de evidência automatizado ainda — Attested.
        Define("OPS.DASHBOARDS_ALERTS", ReadinessGateGroup.Operations, ReadinessControlEvidenceSource.Attested,
            "Dashboards e alertas (§47.4)."),
        Define("OPS.ONCALL_ESCALATION", ReadinessGateGroup.Operations, ReadinessControlEvidenceSource.Attested,
            "On-call e escalation (§47.4)."),
        Define("OPS.DLQ_RETRY_QUARANTINE_RUNBOOKS", ReadinessGateGroup.Operations, ReadinessControlEvidenceSource.Attested,
            "DLQ/retry/quarantine runbooks (§47.4)."),
        Define("OPS.CAPACITY_FINOPS", ReadinessGateGroup.Operations, ReadinessControlEvidenceSource.Attested,
            "Capacity/FinOps (§47.4)."),
        Define("OPS.RTO_EXERCISED", ReadinessGateGroup.Operations, ReadinessControlEvidenceSource.SystemDerived,
            "RTO exercitado (§47.4, objetivo ControlPlaneRto) — resolvido a partir do restore drill (AB-I7-005)."),
        Define("OPS.RPO_EXERCISED", ReadinessGateGroup.Operations, ReadinessControlEvidenceSource.SystemDerived,
            "RPO exercitado (§47.4, objetivos ControlPlaneRpo/EvidenceLogicalRpo) — permanece estruturalmente " +
            "Blocked/NotMeasured nesta baseline (AB-I7-007 item 2: nenhum drill de failure-boundary real existe ainda)."),
        Define("OPS.SUPPORT_PACKAGE_AUTOMATION", ReadinessGateGroup.Operations, ReadinessControlEvidenceSource.Attested,
            "Support package automation (§47.4)."),

        // §47.5 — Gate Microsoft 365. Os dois limites numéricos hard-coded no domínio (100 GB/500 linhas) e
        // a rejeição de target root "/" são invariantes de código verificáveis em runtime — SystemDerived
        // via auto-checagem determinística (ProductionReadinessPolicyInvariants), nunca I/O externo.
        // TENANT_PRECHECK/AZCOPY_VERSION_HOMOLOGATED/MAPPING_VALIDATOR (AB-I8-002 blocker 2/catálogo
        // revisado) têm fonte canônica real produzida no I5: o snapshot mais recente do tenant/projeto —
        // independentemente de qual mailbox/pedido/onda especificamente o produziu, já que este review não é
        // escopado a uma onda — resolvido via IMailboxPrecheckStore/IPurviewUploadAttemptStore/
        // IMappingValidationStore. ARCHIVE_LICENSE_QUOTA e MINIMUM_ROLES permanecem Attested: nenhum store de
        // evidência de license/quota de archive existe hoje no repositório (nenhum tipo/tabela representa
        // este conceito), e roles mínimas do tenant são checklist operacional sem store dedicado.
        Define("M365.MINIMUM_ROLES", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.Attested,
            "Roles mínimas (§47.5)."),
        Define("M365.TENANT_PRECHECK", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.SystemDerived,
            "Tenant precheck (§47.5) — resolvido a partir do precheck de mailbox mais recente já registrado no " +
            "tenant/projeto (IMailboxPrecheckStore, I5); ausente/ArchiveStatus != Active nunca é Pass."),
        Define("M365.ARCHIVE_LICENSE_QUOTA", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.Attested,
            "Archive/licença/quota (§47.5) — nenhum store de evidência de license/quota de archive existe hoje " +
            "neste repositório; permanece Attested até um incremento futuro introduzir essa evidência canônica."),
        Define("M365.AZCOPY_VERSION_HOMOLOGATED", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.SystemDerived,
            "AzCopy version homologada (§47.5) — resolvido a partir da tentativa de upload Uploaded mais recente já " +
            "registrada no tenant/projeto (IPurviewUploadAttemptStore, I5), cruzando o binário observado contra o " +
            "catálogo de binários homologados (AzCopyHomologationCatalog); binário desconhecido/divergente nunca é Pass."),
        Define("M365.MAPPING_VALIDATOR", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.SystemDerived,
            "Mapping validator (§47.5) — resolvido a partir da tentativa de validação de mapping mais recente já " +
            "registrada no tenant/projeto (IMappingValidationStore, Slice 4a); Invalid/Rejected/ausente nunca é Pass."),
        Define("M365.TARGET_ROOT_POLICY", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.SystemDerived,
            "Target root policy (§47.5) — resolvido verificando em runtime que TargetRootFolder rejeita a raiz \"/\"."),
        Define("M365.IMPORT_LIMITS_100GB_500ROWS", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.SystemDerived,
            "Limite 100 GB/500 linhas (§47.5) — resolvido comparando os limites configurados do policy gate contra os valores documentados."),
        Define("M365.PORTAL_OPERATOR_TRAINED", ReadinessGateGroup.Microsoft365, ReadinessControlEvidenceSource.Attested,
            "Portal operator treinado (§47.5)."),
    ];

    private static readonly Dictionary<ReadinessControlId, ReadinessControlDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id);

    /// <summary>Todos os controles do catálogo, na ordem declarada acima (determinística).</summary>
    public static IReadOnlyList<ReadinessControlDefinition> AllControls { get; } = Definitions;

    /// <summary>Todos os controles de um grupo específico, na ordem declarada.</summary>
    public static IReadOnlyList<ReadinessControlDefinition> ControlsForGroup(ReadinessGateGroup group) =>
        [.. Definitions.Where(definition => definition.Group == group)];

    /// <summary>A definição FIXA de um controle — nunca fornecida/alterável pelo chamador.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="controlId"/> não pertence a este catálogo.</exception>
    public static ReadinessControlDefinition Definition(ReadinessControlId controlId)
    {
        if (!ById.TryGetValue(controlId, out var definition))
        {
            throw new ArgumentOutOfRangeException(nameof(controlId), controlId.Value, "Controle de readiness desconhecido neste catálogo.");
        }

        return definition;
    }

    /// <summary>Verdadeiro quando <paramref name="controlId"/> pertence a este catálogo.</summary>
    public static bool IsKnown(ReadinessControlId controlId) => ById.ContainsKey(controlId);

    private static ReadinessControlDefinition Define(
        string id, ReadinessGateGroup group, ReadinessControlEvidenceSource evidenceSource, string description) =>
        new(new ReadinessControlId(id), group, evidenceSource, description);
}
