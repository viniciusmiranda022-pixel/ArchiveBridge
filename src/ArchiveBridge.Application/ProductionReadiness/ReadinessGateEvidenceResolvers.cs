using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.Security;

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
