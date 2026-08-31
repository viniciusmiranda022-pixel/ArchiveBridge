using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Application.ProductionReadiness;

/// <summary>
/// Resolve, para cada controle <see cref="ReadinessControlEvidenceSource.SystemDerived"/> do catálogo, o
/// <see cref="ReadinessControlResult"/> a partir de evidência canônica JÁ PERSISTIDA pelos incrementos
/// anteriores (I6/I7) — nunca fabrica <see cref="ReadinessControlStatus.Pass"/> quando o store subjacente
/// não tem registro algum, nunca chama Purview/Graph/EXO/AzCopy/host real (STOP-THE-LINE do work order
/// AB-I8-001). Cada leitura passa pelo store canônico (<c>GetLatestAsync</c>), que já revalida tamper-
/// evidence (<c>Rehydrate</c>) antes de devolver qualquer registro — nenhuma evidência adulterada chega
/// jamais a este resolver.
/// </summary>
internal static class ReadinessGateEvidenceResolvers
{
    /// <summary>
    /// Resolve <c>PolicyVersionFingerprint</c> do snapshot INTEIRAMENTE server-side (AB-I8-002 blocker 1) —
    /// nunca aceito do caller. Nenhum registro dedicado de "policy version" existe hoje neste repositório
    /// (nenhuma fonte canônica única); em vez de fabricar evidência para um registro inexistente, este
    /// fingerprint é composto deterministicamente a partir de fontes que JÁ SÃO canônicas e JÁ SÃO resolvidas
    /// server-side neste mesmo use case: a policy WDAC/App Control vigente do tenant/projeto (<see cref="IWdacPolicyEvidenceStore"/>,
    /// mesma evidência usada por SEC.WDAC_DEFENDER_PATCHING) e os dois invariantes de policy M365 verificados
    /// em runtime (<see cref="ProductionReadinessPolicyInvariants"/>). Qualquer mudança real em qualquer uma
    /// dessas fontes muda este fingerprint, disparando supersession (AB-I8-001 escopo item 7) — nunca um
    /// valor arbitrário alegado pelo caller.
    /// </summary>
    public static async Task<Sha256Hash> ResolvePolicyVersionFingerprintAsync(
        IWdacPolicyEvidenceStore wdacStore,
        TenantScope scope,
        IReadOnlyList<ReadinessControlResult> policyInvariantResults,
        CancellationToken cancellationToken)
    {
        var wdacPolicy = await wdacStore.GetLatestAsync(scope, cancellationToken).ConfigureAwait(false);
        var parts = new List<string>
        {
            "archivebridge.production-readiness.policy-version-fingerprint.v1",
            wdacPolicy is null ? "missing" : wdacPolicy.PolicyVersion.ToString(CultureInfo.InvariantCulture),
            wdacPolicy?.ContentFingerprint.Value ?? "missing",
        };

        // Ordem fixa e determinística (nunca a ordem de entrada do chamador) — mesmo princípio de
        // ProductionReadinessReviewSnapshot.ComputeReviewFingerprint.
        foreach (var result in policyInvariantResults.OrderBy(result => result.ControlId.Value, StringComparer.Ordinal))
        {
            parts.Add(result.ControlId.Value);
            parts.Add(result.Evidence.Fingerprint.Value);
        }

        return DeterministicHash.Compute(parts);
    }

    /// <summary>SEC.PENTEST_NO_OPEN_CRITICAL_HIGH — <see cref="PenTestReadinessStatus"/> NUNCA possui um caso Pass/concluído (bloqueio estrutural do tipo, não deste resolver).</summary>
    public static async Task<ReadinessControlResult> ResolvePenTestAsync(
        IPenTestReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH");
        var bundle = await store.GetLatestAsync(scope, cancellationToken).ConfigureAwait(false);
        if (bundle is null)
        {
            return ReadinessControlResult.NotMeasured(controlId, ReadinessGateGroup.Security, "PENTEST_READINESS_NOT_PREPARED", now);
        }

        var status = bundle.Status switch
        {
            PenTestReadinessStatus.NotPerformed => ReadinessControlStatus.NotPerformed,
            PenTestReadinessStatus.Blocked => ReadinessControlStatus.Blocked,
            _ => ReadinessControlStatus.NotMeasured,
        };
        var evidence = ReadinessEvidenceReference.SystemDerived(
            bundle.ContentFingerprint, $"pentest-readiness:v{bundle.BundleVersion.ToString(CultureInfo.InvariantCulture)}");
        var reasonCode = status == ReadinessControlStatus.Blocked ? "PENTEST_READINESS_BLOCKED" : "PENTEST_NOT_PERFORMED";
        return ReadinessControlResult.Create(controlId, ReadinessGateGroup.Security, status, evidence, reasonCode, bundle.PreparedAtUtc);
    }

    /// <summary>SEC.WDAC_DEFENDER_PATCHING — todos os controles Required da baseline em Pass E uma WDAC policy vigente.</summary>
    public static async Task<ReadinessControlResult> ResolveWdacDefenderPatchingAsync(
        IWorkerHardeningBaselineStore hardeningStore,
        IWdacPolicyEvidenceStore wdacStore,
        TenantScope scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("SEC.WDAC_DEFENDER_PATCHING");
        var records = await hardeningStore.GetLatestForAllControlsAsync(scope, cancellationToken).ConfigureAwait(false);
        var byControl = records.ToDictionary(record => record.Control);

        var requiredControls = WorkerHardeningBaselineCatalog.AllControls
            .Where(control => WorkerHardeningBaselineCatalog.Applicability(control) == WorkerHardeningApplicability.Required)
            .ToList();

        var anyMissing = requiredControls.Any(control => !byControl.ContainsKey(control));
        var anyNotPass = requiredControls.Any(control =>
            byControl.TryGetValue(control, out var record) && record.Status != WorkerHardeningStatus.Pass);

        var wdacPolicy = await wdacStore.GetLatestAsync(scope, cancellationToken).ConfigureAwait(false);

        var fingerprintParts = new List<string> { "archivebridge.production-readiness.wdac-defender-patching.v1" };
        foreach (var control in requiredControls)
        {
            fingerprintParts.Add(byControl.TryGetValue(control, out var record) ? record.ContentFingerprint.Value : "missing");
        }

        fingerprintParts.Add(wdacPolicy?.ContentFingerprint.Value ?? "missing");
        var combinedFingerprint = DeterministicHash.Compute(fingerprintParts);
        var locator = $"worker-hardening+wdac-policy:{requiredControls.Count.ToString(CultureInfo.InvariantCulture)}-required-controls";
        var evidence = ReadinessEvidenceReference.SystemDerived(combinedFingerprint, locator);
        var observedAt = records.Count > 0 ? records.Max(record => record.ExecutedAtUtc) : now;

        if (wdacPolicy is null || anyMissing)
        {
            return ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Security, ReadinessControlStatus.NotMeasured, evidence,
                "WDAC_DEFENDER_PATCHING_EVIDENCE_MISSING", observedAt);
        }

        if (anyNotPass)
        {
            return ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Security, ReadinessControlStatus.Blocked, evidence,
                "WDAC_DEFENDER_PATCHING_CONTROL_NOT_PASS", observedAt);
        }

        return ReadinessControlResult.Create(
            controlId, ReadinessGateGroup.Security, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, observedAt);
    }

    /// <summary>SEC.SBOM_AND_SIGNATURES — a build provenance aprovada do artifact revisado corresponde exatamente ao digest/commit sob revisão (mesma verificação de <see cref="ArtifactPromotionVerifier"/>).</summary>
    public static async Task<ReadinessControlResult> ResolveSbomAndSignaturesAsync(
        IBuildProvenanceStore store,
        TenantScope scope,
        string artifactName,
        string reviewedCommitSha,
        Sha256Hash reviewedArtifactDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("SEC.SBOM_AND_SIGNATURES");
        var approved = await store.GetLatestAsync(scope, artifactName, cancellationToken).ConfigureAwait(false);
        if (approved is null)
        {
            return ReadinessControlResult.NotMeasured(controlId, ReadinessGateGroup.Security, "BUILD_PROVENANCE_NOT_APPROVED", now);
        }

        var evidence = ReadinessEvidenceReference.SystemDerived(
            approved.ContentFingerprint, $"build-provenance:{artifactName}:v{approved.ArtifactVersion.ToString(CultureInfo.InvariantCulture)}");

        var digestMatches = string.Equals(approved.ArtifactDigest.Value, reviewedArtifactDigest.Value, StringComparison.Ordinal);
        var commitMatches = string.Equals(approved.SourceCommitSha, reviewedCommitSha, StringComparison.OrdinalIgnoreCase);

        if (!digestMatches || !commitMatches)
        {
            // Drift entre a build aprovada e o build efetivamente sob revisão — nunca aceito silenciosamente
            // (AB-I8-001 escopo item 7/acceptance criteria 5: build digest alterado invalida readiness).
            return ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Security, ReadinessControlStatus.Fail, evidence,
                "BUILD_PROVENANCE_DRIFT_FROM_REVIEWED_BUILD", approved.ApprovedAtUtc);
        }

        return ReadinessControlResult.Create(
            controlId, ReadinessGateGroup.Security, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, approved.ApprovedAtUtc);
    }

    /// <summary>SEC.INCIDENT_RESPONSE_EXERCISED — os três drills sintéticos (AB-I7-008) todos <see cref="IncidentResponseDrillOutcome.Contained"/>.</summary>
    public static async Task<ReadinessControlResult> ResolveIncidentResponseAsync(
        IIncidentResponseDrillStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("SEC.INCIDENT_RESPONSE_EXERCISED");
        var drillTypes = Enum.GetValues<IncidentResponseDrillType>();
        var records = new List<IncidentResponseDrillRecord>();
        foreach (var drillType in drillTypes)
        {
            var record = await store.GetLatestAsync(scope, drillType, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        var fingerprint = DeterministicHash.Compute(
            ["archivebridge.production-readiness.incident-response.v1", .. records.Select(record => record.ContentFingerprint.Value)]);
        var evidence = ReadinessEvidenceReference.SystemDerived(fingerprint, $"incident-response-drills:{records.Count.ToString(CultureInfo.InvariantCulture)}-of-3");
        var observedAt = records.Count > 0 ? records.Max(record => record.RecordedAtUtc) : now;

        if (records.Count < drillTypes.Length)
        {
            return ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Security, ReadinessControlStatus.NotMeasured, evidence,
                "INCIDENT_RESPONSE_DRILL_MISSING", observedAt);
        }

        var anyFailed = records.Any(record => record.Outcome != IncidentResponseDrillOutcome.Contained);
        return anyFailed
            ? ReadinessControlResult.Create(controlId, ReadinessGateGroup.Security, ReadinessControlStatus.Fail, evidence, "INCIDENT_RESPONSE_DRILL_FAILED", observedAt)
            : ReadinessControlResult.Create(controlId, ReadinessGateGroup.Security, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, observedAt);
    }

    /// <summary>OPS.RTO_EXERCISED — restore drill com objetivo ControlPlaneRto.</summary>
    public static Task<ReadinessControlResult> ResolveRtoAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        ResolveRecoveryObjectiveAsync(
            store, scope, new ReadinessControlId("OPS.RTO_EXERCISED"), ReadinessGateGroup.Operations,
            RecoveryExerciseType.RestoreDrill, RecoveryObjective.ControlPlaneRto, "RTO_NOT_EXERCISED", now, cancellationToken);

    /// <summary>
    /// OPS.RPO_EXERCISED — verifica os dois objetivos de RPO (ControlPlaneRpo via RestoreDrill,
    /// EvidenceLogicalRpo via ArtifactEvidenceRecovery). <see cref="RecoveryReadinessRecord.Pass"/> lança
    /// para ambos nesta baseline (AB-I7-007 item 2) — este controle é estruturalmente incapaz de ser
    /// <see cref="ReadinessControlStatus.Pass"/> até um incremento futuro introduzir um drill de
    /// failure-boundary real.
    /// </summary>
    public static async Task<ReadinessControlResult> ResolveRpoAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlPlaneRpo = await ResolveRecoveryObjectiveAsync(
            store, scope, new ReadinessControlId("OPS.RPO_EXERCISED"), ReadinessGateGroup.Operations,
            RecoveryExerciseType.RestoreDrill, RecoveryObjective.ControlPlaneRpo, "RPO_NOT_EXERCISED", now, cancellationToken)
            .ConfigureAwait(false);

        if (controlPlaneRpo.Status != ReadinessControlStatus.NotMeasured)
        {
            return controlPlaneRpo;
        }

        return await ResolveRecoveryObjectiveAsync(
            store, scope, new ReadinessControlId("OPS.RPO_EXERCISED"), ReadinessGateGroup.Operations,
            RecoveryExerciseType.ArtifactEvidenceRecovery, RecoveryObjective.EvidenceLogicalRpo, "RPO_NOT_EXERCISED", now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>DATA.BACKUP_RESTORE_TESTED — qualquer restore drill vigente, independentemente do objetivo específico medido.</summary>
    public static async Task<ReadinessControlResult> ResolveBackupRestoreAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("DATA.BACKUP_RESTORE_TESTED");
        var record = await store.GetLatestAsync(scope, RecoveryExerciseType.RestoreDrill, cancellationToken).ConfigureAwait(false);
        return MapRecoveryRecord(record, controlId, ReadinessGateGroup.Data, "BACKUP_RESTORE_NOT_TESTED", now);
    }

    /// <summary>DATA.HASHES_MANIFESTS_LINEAGE_WORM — exercício de artifact/evidence recovery (revalidação de hash/manifesto/lineage/certificate após restore).</summary>
    public static async Task<ReadinessControlResult> ResolveHashesManifestsLineageAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("DATA.HASHES_MANIFESTS_LINEAGE_WORM");
        var record = await store.GetLatestAsync(scope, RecoveryExerciseType.ArtifactEvidenceRecovery, cancellationToken).ConfigureAwait(false);
        return MapRecoveryRecord(record, controlId, ReadinessGateGroup.Data, "ARTIFACT_EVIDENCE_RECOVERY_NOT_EXERCISED", now);
    }

    /// <summary>Rotas de capability conhecidas por este agregador — hoje só a rota GA de PST import (I5); nenhuma rota é alegada além das já modeladas por <see cref="PurviewCapabilityRoutes"/>.</summary>
    private static readonly IReadOnlyList<PurviewCapabilityRoute> KnownCapabilityRoutes = [PurviewCapabilityRoutes.PstImport];

    /// <summary>ARCH.CAPABILITY_MATRIX_CURRENT — cada rota conhecida precisa de evidência <see cref="CapabilityUsabilityOutcome.Usable"/> (GA, dentro da janela de frescor); Unknown/Unsupported/Preview/Contractual/ausente/stale nunca é Pass (mesma política já usada pelo precheck gate real, AB-I5-001/<see cref="CapabilityEvidencePolicy"/>).</summary>
    public static async Task<ReadinessControlResult> ResolveCapabilityMatrixAsync(
        ICapabilityEvidenceStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("ARCH.CAPABILITY_MATRIX_CURRENT");
        var fingerprintParts = new List<string> { "archivebridge.production-readiness.capability-matrix.v1" };
        var worstOutcome = CapabilityUsabilityOutcome.Usable;
        DateTimeOffset? latestObservedAt = null;

        foreach (var route in KnownCapabilityRoutes)
        {
            var latest = await store.GetLatestAsync(scope, TargetProvider.Purview, route, cancellationToken).ConfigureAwait(false);
            var outcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(latest, now, CapabilityEvidencePolicy.DefaultMaxAge);

            fingerprintParts.Add(route.Value);
            fingerprintParts.Add(outcome.ToString());
            fingerprintParts.Add(latest?.EvidenceHash.Value ?? "missing");

            if (latest is not null && (latestObservedAt is null || latest.RecordedAtUtc > latestObservedAt))
            {
                latestObservedAt = latest.RecordedAtUtc;
            }

            // Pior desfecho vence: uma rota Unknown/stale/não-GA já basta para bloquear o controle inteiro,
            // mesmo que outras rotas estejam Usable — nunca "média" nem "melhor caso".
            if (RankCapabilityOutcome(outcome) > RankCapabilityOutcome(worstOutcome))
            {
                worstOutcome = outcome;
            }
        }

        var fingerprint = DeterministicHash.Compute(fingerprintParts);
        var evidence = ReadinessEvidenceReference.SystemDerived(
            fingerprint, $"capability-evidence:{KnownCapabilityRoutes.Count.ToString(CultureInfo.InvariantCulture)}-routes");
        var observedAt = latestObservedAt ?? now;

        return worstOutcome switch
        {
            CapabilityUsabilityOutcome.Usable =>
                ReadinessControlResult.Create(controlId, ReadinessGateGroup.Architecture, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, observedAt),
            CapabilityUsabilityOutcome.NoEvidence =>
                ReadinessControlResult.Create(controlId, ReadinessGateGroup.Architecture, ReadinessControlStatus.NotMeasured, evidence, "CAPABILITY_EVIDENCE_MISSING", observedAt),
            CapabilityUsabilityOutcome.Stale =>
                ReadinessControlResult.Create(controlId, ReadinessGateGroup.Architecture, ReadinessControlStatus.NotMeasured, evidence, "CAPABILITY_EVIDENCE_STALE", observedAt),
            CapabilityUsabilityOutcome.Unsupported =>
                ReadinessControlResult.Create(controlId, ReadinessGateGroup.Architecture, ReadinessControlStatus.Fail, evidence, "CAPABILITY_UNSUPPORTED", observedAt),
            // Unknown (fail-closed default do CapabilityStatus) e NotGeneralAvailability (Preview/Contractual,
            // nunca promovida implicitamente a GA) bloqueiam — nunca Pass por omissão (AB-I8-001 escopo item 6).
            _ =>
                ReadinessControlResult.Create(
                    controlId, ReadinessGateGroup.Architecture, ReadinessControlStatus.Blocked,
                    evidence, worstOutcome == CapabilityUsabilityOutcome.Unknown ? "CAPABILITY_STATUS_UNKNOWN" : "CAPABILITY_NOT_GENERAL_AVAILABILITY", observedAt),
        };
    }

    // Ordem de severidade para "pior desfecho vence" — nunca reflete a ordem de declaração do enum, que é
    // documental (CapabilityEvidencePolicy), não uma escala de risco.
    private static int RankCapabilityOutcome(CapabilityUsabilityOutcome outcome) => outcome switch
    {
        CapabilityUsabilityOutcome.Usable => 0,
        CapabilityUsabilityOutcome.NoEvidence => 1,
        CapabilityUsabilityOutcome.Stale => 1,
        CapabilityUsabilityOutcome.NotGeneralAvailability => 2,
        CapabilityUsabilityOutcome.Unknown => 3,
        CapabilityUsabilityOutcome.Unsupported => 4,
        _ => 4,
    };

    /// <summary>
    /// M365.TENANT_PRECHECK — precheck de mailbox mais recente já registrado em QUALQUER archive deste
    /// tenant/projeto (o review não é escopado a uma onda/mailbox específica, AB-I8-002 blocker 2). Ausente
    /// ou <see cref="MailboxArchiveStatus"/> diferente de <see cref="MailboxArchiveStatus.Active"/> nunca é Pass.
    /// </summary>
    public static async Task<ReadinessControlResult> ResolveTenantPrecheckAsync(
        IMailboxPrecheckStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("M365.TENANT_PRECHECK");
        var snapshot = await store.GetLatestAcrossMailboxesAsync(scope, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return ReadinessControlResult.NotMeasured(controlId, ReadinessGateGroup.Microsoft365, "TENANT_PRECHECK_NOT_PERFORMED", now);
        }

        var evidence = ReadinessEvidenceReference.SystemDerived(snapshot.SnapshotHash, $"mailbox-precheck:{snapshot.Id.Value}");
        if (snapshot.ArchiveStatus != MailboxArchiveStatus.Active)
        {
            return ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Blocked, evidence,
                "TENANT_PRECHECK_ARCHIVE_NOT_ACTIVE", snapshot.RecordedAtUtc);
        }

        return ReadinessControlResult.Create(
            controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, snapshot.RecordedAtUtc);
    }

    /// <summary>
    /// M365.MAPPING_VALIDATOR — tentativa de validação de mapping mais recente já registrada neste tenant/
    /// projeto (não escopada a uma onda específica, AB-I8-002 blocker 2). Ausente, <see cref="MappingValidationAttemptOutcome.Invalid"/>
    /// ou <see cref="MappingValidationAttemptOutcome.Rejected"/> nunca é Pass.
    /// </summary>
    public static async Task<ReadinessControlResult> ResolveMappingValidatorAsync(
        IMappingValidationStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("M365.MAPPING_VALIDATOR");
        var attempt = await store.GetLatestAsync(scope, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return ReadinessControlResult.NotMeasured(controlId, ReadinessGateGroup.Microsoft365, "MAPPING_VALIDATION_NOT_PERFORMED", now);
        }

        var evidence = ReadinessEvidenceReference.SystemDerived(attempt.ContentSha256, $"mapping-validation-attempt:{attempt.ValidationId}");
        return attempt.Outcome switch
        {
            MappingValidationAttemptOutcome.Valid =>
                ReadinessControlResult.Create(controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, attempt.CreatedAtUtc),
            MappingValidationAttemptOutcome.Invalid =>
                ReadinessControlResult.Create(controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Fail, evidence, "MAPPING_VALIDATION_INVALID", attempt.CreatedAtUtc),
            // Rejected = conteúdo recebido mas não validável semanticamente (encoding/BOM) — bloqueia, nunca
            // um Fail definitivo (o problema pode estar no arquivo enviado, não necessariamente no mapping).
            _ =>
                ReadinessControlResult.Create(controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Blocked, evidence, "MAPPING_VALIDATION_REJECTED", attempt.CreatedAtUtc),
        };
    }

    /// <summary>
    /// M365.AZCOPY_VERSION_HOMOLOGATED — tentativa de upload <see cref="PurviewUploadAttemptOutcome.Uploaded"/>
    /// mais recente já registrada neste tenant/projeto (não escopada a uma onda/pedido específico, AB-I8-002
    /// blocker 2), cruzando o binário observado contra <paramref name="homologatedBinaries"/> — mesma
    /// verificação exata (versão E hash) já usada pelo executor real (<see cref="AzCopyHomologationCatalog.IsHomologated"/>).
    /// Ausente ou binário desconhecido/divergente nunca é Pass.
    /// </summary>
    public static async Task<ReadinessControlResult> ResolveAzCopyHomologationAsync(
        IPurviewUploadAttemptStore store, AzCopyHomologationCatalog homologatedBinaries, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var controlId = new ReadinessControlId("M365.AZCOPY_VERSION_HOMOLOGATED");
        var record = await store.GetLatestAcrossRequestsAsync(scope, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Outcome != PurviewUploadAttemptOutcome.Uploaded || record.Evidence is not { } uploadEvidence)
        {
            return ReadinessControlResult.NotMeasured(controlId, ReadinessGateGroup.Microsoft365, "AZCOPY_UPLOAD_NOT_PERFORMED", now);
        }

        var fingerprint = DeterministicHash.Compute(
        [
            "archivebridge.production-readiness.azcopy-homologation.v1",
            uploadEvidence.Binary.Version,
            uploadEvidence.Binary.Sha256.Value,
            uploadEvidence.ManifestHash.Value,
        ]);
        var evidence = ReadinessEvidenceReference.SystemDerived(fingerprint, $"purview-upload-attempt:{record.Attempt.Value}");

        if (!homologatedBinaries.IsHomologated(uploadEvidence.Binary))
        {
            return ReadinessControlResult.Create(
                controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Blocked, evidence,
                "AZCOPY_BINARY_NOT_HOMOLOGATED", record.CompletedAtUtc);
        }

        return ReadinessControlResult.Create(
            controlId, ReadinessGateGroup.Microsoft365, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, record.CompletedAtUtc);
    }

    private static async Task<ReadinessControlResult> ResolveRecoveryObjectiveAsync(
        IRecoveryReadinessStore store,
        TenantScope scope,
        ReadinessControlId controlId,
        ReadinessGateGroup group,
        RecoveryExerciseType exerciseType,
        RecoveryObjective objective,
        string missingReasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = await store.GetLatestAsync(scope, exerciseType, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Objective != objective)
        {
            return ReadinessControlResult.NotMeasured(controlId, group, missingReasonCode, now);
        }

        return MapRecoveryRecord(record, controlId, group, missingReasonCode, now);
    }

    private static ReadinessControlResult MapRecoveryRecord(
        RecoveryReadinessRecord? record, ReadinessControlId controlId, ReadinessGateGroup group, string missingReasonCode, DateTimeOffset now)
    {
        if (record is null)
        {
            return ReadinessControlResult.NotMeasured(controlId, group, missingReasonCode, now);
        }

        var status = record.Status switch
        {
            RecoveryReadinessStatus.Pass => ReadinessControlStatus.Pass,
            RecoveryReadinessStatus.Blocked => ReadinessControlStatus.Blocked,
            _ => ReadinessControlStatus.NotMeasured,
        };
        var evidence = ReadinessEvidenceReference.SystemDerived(
            record.EvidenceFingerprint, $"recovery-readiness:{record.ExerciseType}:v{record.ExerciseVersion.ToString(CultureInfo.InvariantCulture)}");
        var reasonCode = status == ReadinessControlStatus.NotMeasured ? missingReasonCode : status == ReadinessControlStatus.Blocked ? "RECOVERY_OBJECTIVE_NOT_MET" : string.Empty;
        return ReadinessControlResult.Create(controlId, group, status, evidence, reasonCode, record.ExecutedAtUtc);
    }
}
