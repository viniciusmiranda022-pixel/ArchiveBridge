namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Catálogo FIXO e versionado dos dez cenários obrigatórios do canário de produção (AB-I8-004, runbook §48)
/// — a ÚNICA fonte de verdade de quais cenários existem e como podem ser resolvidos
/// (<see cref="CanaryScenarioEvidenceSource"/>). Cada entrada corresponde 1:1 a um item literal do §48
/// (fonte de autoridade citada no work order) — nenhum cenário aspiracional adicional. Nenhum chamador
/// informa a lista de cenários ou sua classe de evidência: tudo é sempre derivado daqui (mesmo princípio de
/// <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlCatalog"/>), de forma que nenhuma
/// identidade/papel possa "inventar" um cenário novo que sempre passa, nem reclassificar um cenário
/// <see cref="CanaryScenarioEvidenceSource.SystemDerived"/> como atestável por operador.
/// </summary>
public static class CanaryScenarioCatalog
{
    /// <summary>Versão do catálogo — gravada em todo plano novo, nunca reescrita.</summary>
    public const string CurrentCatalogVersion = "archivebridge.canary.scenario-catalog.v1";

    // §48 item 176 — tenant controlado, mailbox de teste licenciada. Fonte canônica real já existente
    // (IMailboxPrecheckStore, I5) — mesma resolução de M365.TENANT_PRECHECK em ReadinessControlCatalog.
    private static readonly CanaryScenarioDefinition TenantMailboxControlled = Define(
        "CANARY.TENANT_MAILBOX_CONTROLLED", CanaryScenarioEvidenceSource.SystemDerived,
        "Tenant controlado e mailbox de teste licenciada (§48 item 176) — resolvido a partir do precheck de " +
        "mailbox mais recente já registrado no tenant/projeto (IMailboxPrecheckStore); ausente/ArchiveStatus " +
        "!= Active nunca é Pass.");

    // §48 item 177 — 20 tipos de item e propriedades customizadas. Nenhum store canônico conta diversidade
    // de tipos de item hoje neste repositório — Operator-attested (mesmo princípio de controles Attested sem
    // store dedicado em ReadinessControlCatalog). AB-I8-006: a evidência de Pass exige um digest/locator
    // OPACO a evidência independentemente retida (ex.: hash de um corpus-report exportado), nunca apenas o
    // texto autoral do próprio operador tratado como prova — reforçado por
    // SubmitCanaryScenarioEvidenceUseCase, nunca por este catálogo.
    private static readonly CanaryScenarioDefinition CorpusItemTypeDiversity = Define(
        "CANARY.CORPUS_ITEM_TYPE_DIVERSITY", CanaryScenarioEvidenceSource.OperatorAttested,
        "Corpus com 20 tipos de item e propriedades customizadas (§48 item 177).");

    // §48 item 178 — PST pequeno, depois boundary de 18 GB. AB-I8-006: resolvido a partir de DUAS
    // PstInspectionRecord canônicas já persistidas (Slice 4B/IPstInspectionStore.FindCanonicalAsync) — o
    // caller informa os DOIS artefatos candidatos (pequeno + boundary); o resolver nunca aceita o veredito
    // do caller, apenas os ObservedSizeBytes REAIS de cada inspeção canônica. AB-I8-007: o lado "boundary" é
    // verificado contra o ÚNICO limiar de 18 GB REALMENTE documentado no repositório
    // (PartitionPolicy.RunbookTargetPartBytes, runbook §16.3/§20.1); o lado "pequeno" não tem limiar numérico
    // documentado em lugar algum, então nunca é inventado — o cenário permanece estruturalmente
    // Blocked/NotPerformed até que um critério documentado para "PST pequeno" exista, nunca Pass por
    // afirmação do operador nem por aproximação de engenharia.
    private static readonly CanaryScenarioDefinition PstSizeBoundaryCoverage = Define(
        "CANARY.PST_SIZE_BOUNDARY_COVERAGE", CanaryScenarioEvidenceSource.SystemDerived,
        "PST pequeno e PST no boundary de 18 GB (§48 item 178) — resolvido a partir do tamanho observado de " +
        "duas PstInspectionRecord canônicas (IPstInspectionStore); permanece Blocked até existir critério " +
        "documentado para \"PST pequeno\".");

    // §48 item 179 — replay do mesmo PST no mesmo target root. AB-I8-006: resolvido a partir da história
    // REAL de tentativas de upload (IPurviewUploadAttemptStore) do pedido canônico da wave — exige
    // evidência de que MAIS de uma tentativa foi de fato despachada (retry/réplay real ocorreu, nunca
    // apenas "nunca reexecutado") e que, apesar disso, existe EXATAMENTE UMA tentativa Uploaded (nenhum
    // efeito externo duplicado) — nunca aceita o status alegado pelo operador.
    private static readonly CanaryScenarioDefinition ReplaySameTargetRootIdempotent = Define(
        "CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT", CanaryScenarioEvidenceSource.SystemDerived,
        "Replay do mesmo PST no mesmo target root converge sem duplicar efeito (§48 item 179) — resolvido a " +
        "partir da história real de tentativas de upload (IPurviewUploadAttemptStore).");

    // §48 item 180 — target root diferente deve bloquear. AB-I8-006: resolvido exercitando o MESMO guard de
    // domínio real que protege a produção (MigrationWave.ChangeTargetRootFolder, congelado após aprovação)
    // contra um root candidato diferente informado pelo caller — nunca persiste a mutação tentada, apenas
    // observa deterministicamente se InvalidWaveTransitionException é lançada pelo estado REAL da wave.
    private static readonly CanaryScenarioDefinition DifferentTargetRootBlocks = Define(
        "CANARY.DIFFERENT_TARGET_ROOT_BLOCKS", CanaryScenarioEvidenceSource.SystemDerived,
        "Tentativa deliberada com target root diferente é bloqueada (§48 item 180) — resolvido exercitando o " +
        "guard real de congelamento de destino da wave (MigrationWave.ChangeTargetRootFolder).");

    // §48 item 181 — corrupção conhecida e quarantine. AB-I8-006: nenhum store de "quarantine" dedicado
    // existe hoje neste repositório (runbook §22.3 ainda não implementado; grep repo-wide confirma ausência
    // de qualquer store/estado/ação). AB-I8-006 havia estreitado o SIGNIFICADO do cenário para "nunca se
    // tornou elegível a transporte" e ainda assim emitido Pass — AB-I8-007 rejeitou isso: o item 181 exige
    // quarantine, não apenas diagnóstico. Resolvido a partir de IPstInspectionStore.FindCanonicalAsync, mas
    // NUNCA emite Pass hoje: mesmo com corrupção diagnosticada server-side (evidência canônica anexada), o
    // resultado é Blocked até que um mecanismo de quarantine real exista e possa ser verificado.
    private static readonly CanaryScenarioDefinition KnownCorruptionQuarantine = Define(
        "CANARY.KNOWN_CORRUPTION_QUARANTINE", CanaryScenarioEvidenceSource.SystemDerived,
        "Corrupção conhecida deve resultar em quarantine (§48 item 181) — resolvido a partir do " +
        "StructuralDiagnostic de uma PstInspectionRecord canônica (IPstInspectionStore); permanece Blocked " +
        "até existir um mecanismo de quarantine real neste repositório, mesmo com corrupção diagnosticada.");

    // §48 item 182 — crash recovery. Fonte canônica real já existente (IRecoveryReadinessStore,
    // RecoveryExerciseType.PendingWorkRebuild, I7).
    private static readonly CanaryScenarioDefinition CrashRecovery = Define(
        "CANARY.CRASH_RECOVERY", CanaryScenarioEvidenceSource.SystemDerived,
        "Crash recovery (§48 item 182) — resolvido a partir do exercício de reconstrução determinística de " +
        "trabalho pendente (IRecoveryReadinessStore, RecoveryExerciseType.PendingWorkRebuild).");

    // §48 item 183 — reconciliação e evidence package. Fonte canônica real já existente
    // (IReconciliationCertificateStore, I6).
    private static readonly CanaryScenarioDefinition ReconciliationEvidencePackage = Define(
        "CANARY.RECONCILIATION_EVIDENCE_PACKAGE", CanaryScenarioEvidenceSource.SystemDerived,
        "Reconciliação e evidence package (§48 item 183) — resolvido a partir do reconciliation certificate " +
        "canônico e vigente (IReconciliationCertificateStore); INCONCLUSIVE/FAIL/DUPLICATE_RISK/evidência " +
        "incompleta nunca é Pass.");

    // §48 item 184 — restore/rollback operacional. Fonte canônica real já existente (IRecoveryReadinessStore,
    // RecoveryExerciseType.RestoreDrill, I7).
    private static readonly CanaryScenarioDefinition RestoreRollbackOperational = Define(
        "CANARY.RESTORE_ROLLBACK_OPERATIONAL", CanaryScenarioEvidenceSource.SystemDerived,
        "Restore/rollback operacional (§48 item 184) — resolvido a partir do restore drill " +
        "(IRecoveryReadinessStore, RecoveryExerciseType.RestoreDrill); permanece Blocked/NotMeasured quando " +
        "nenhum drill compatível e comprovado existe.");

    // §48 item 185 — approval para primeira onda real. Gate de decisão humana final — resolvido
    // EXCLUSIVAMENTE por ApproveCanaryFirstWaveUseCase, nunca pela submissão genérica de evidência.
    private static readonly CanaryScenarioDefinition FirstWaveApproval = Define(
        "CANARY.FIRST_WAVE_APPROVAL", CanaryScenarioEvidenceSource.ApprovalDecision,
        "Approval para primeira onda real de baixa criticidade (§48 item 185) — decisão humana auditável e " +
        "server-side RBAC; apenas autoriza avançar para operational readiness/go-live, nunca marca " +
        "projeto/wave COMPLETED.");

    private static readonly CanaryScenarioDefinition[] Definitions =
    [
        TenantMailboxControlled,
        CorpusItemTypeDiversity,
        PstSizeBoundaryCoverage,
        ReplaySameTargetRootIdempotent,
        DifferentTargetRootBlocks,
        KnownCorruptionQuarantine,
        CrashRecovery,
        ReconciliationEvidencePackage,
        RestoreRollbackOperational,
        FirstWaveApproval,
    ];

    private static readonly Dictionary<CanaryScenarioId, CanaryScenarioDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id);

    /// <summary>Todos os cenários do catálogo, na ordem declarada acima (determinística).</summary>
    public static IReadOnlyList<CanaryScenarioDefinition> AllScenarios { get; } = Definitions;

    /// <summary>Identidade estável do gate de aprovação da primeira onda real (§48 item 185).</summary>
    public static CanaryScenarioId FirstWaveApprovalScenarioId { get; } = FirstWaveApproval.Id;

    /// <summary>A definição FIXA de um cenário — nunca fornecida/alterável pelo chamador.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scenarioId"/> não pertence a este catálogo.</exception>
    public static CanaryScenarioDefinition Definition(CanaryScenarioId scenarioId)
    {
        if (!ById.TryGetValue(scenarioId, out var definition))
        {
            throw new ArgumentOutOfRangeException(nameof(scenarioId), scenarioId.Value, "Cenário de canário desconhecido neste catálogo.");
        }

        return definition;
    }

    /// <summary>Verdadeiro quando <paramref name="scenarioId"/> pertence a este catálogo.</summary>
    public static bool IsKnown(CanaryScenarioId scenarioId) => ById.ContainsKey(scenarioId);

    /// <summary>
    /// Exige que <paramref name="scenarioId"/> seja um cenário conhecido classificado
    /// <see cref="CanaryScenarioEvidenceSource.OperatorAttested"/> — bloqueio estrutural usado pela submissão
    /// genérica de evidência de operador (nunca aceita um cenário SystemDerived ou o gate de aprovação).
    /// </summary>
    /// <exception cref="CanaryScenarioNotAttestableException"><paramref name="scenarioId"/> desconhecido ou não é OperatorAttested.</exception>
    public static CanaryScenarioDefinition RequireOperatorAttestable(CanaryScenarioId scenarioId)
    {
        if (!ById.TryGetValue(scenarioId, out var definition) || definition.EvidenceSource != CanaryScenarioEvidenceSource.OperatorAttested)
        {
            throw new CanaryScenarioNotAttestableException(
                $"O cenário '{scenarioId.Value}' não pode ser atestado por operador (desconhecido, resolvido " +
                "automaticamente, ou é o gate de aprovação da primeira onda).");
        }

        return definition;
    }

    private static CanaryScenarioDefinition Define(string id, CanaryScenarioEvidenceSource evidenceSource, string description) =>
        new(new CanaryScenarioId(id), evidenceSource, description);
}
