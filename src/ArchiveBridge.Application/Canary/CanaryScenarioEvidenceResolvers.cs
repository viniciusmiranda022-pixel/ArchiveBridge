using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Resolve, para cada cenário <see cref="CanaryScenarioEvidenceSource.SystemDerived"/> do catálogo, o
/// <see cref="CanaryScenarioResult"/> a partir de evidência canônica JÁ PERSISTIDA por incrementos anteriores
/// (I5/I6/I7) — nunca fabrica <see cref="CanaryScenarioStatus.Pass"/> quando o store subjacente não tem
/// registro algum, nunca chama Purview/Graph/EXO/AzCopy/host real (STOP-THE-LINE). Cada leitura passa pelo
/// store canônico (<c>GetLatestAsync</c>), que já revalida tamper-evidence antes de devolver qualquer
/// registro — nenhuma evidência adulterada chega jamais a este resolver.
/// </summary>
internal static class CanaryScenarioEvidenceResolvers
{
    private static readonly CanaryScenarioId TenantMailboxControlledId = new("CANARY.TENANT_MAILBOX_CONTROLLED");
    private static readonly CanaryScenarioId CrashRecoveryId = new("CANARY.CRASH_RECOVERY");
    private static readonly CanaryScenarioId ReconciliationEvidencePackageId = new("CANARY.RECONCILIATION_EVIDENCE_PACKAGE");
    private static readonly CanaryScenarioId RestoreRollbackOperationalId = new("CANARY.RESTORE_ROLLBACK_OPERATIONAL");

    /// <summary>CANARY.TENANT_MAILBOX_CONTROLLED — precheck de mailbox mais recente já registrado neste tenant/projeto; ausente ou não Active nunca é Pass.</summary>
    public static async Task<CanaryScenarioResult> ResolveTenantMailboxControlledAsync(
        IMailboxPrecheckStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetLatestAcrossMailboxesAsync(scope, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            // Nunca "nenhuma execução ainda" (Pending): a ausência de QUALQUER precheck significa que o
            // cenário nunca foi de fato exercitado — NotPerformed é o desfecho fail-closed correto aqui.
            return CanaryScenarioResult.Create(
                TenantMailboxControlledId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None,
                "TENANT_MAILBOX_PRECHECK_NOT_PERFORMED", now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(snapshot.SnapshotHash, $"mailbox-precheck:{snapshot.Id.Value}");
        if (snapshot.ArchiveStatus != MailboxArchiveStatus.Active)
        {
            return CanaryScenarioResult.Create(
                TenantMailboxControlledId, CanaryScenarioStatus.Blocked, evidence, "TENANT_MAILBOX_ARCHIVE_NOT_ACTIVE", snapshot.RecordedAtUtc);
        }

        return CanaryScenarioResult.Create(
            TenantMailboxControlledId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, snapshot.RecordedAtUtc);
    }

    /// <summary>CANARY.CRASH_RECOVERY — exercício de reconstrução determinística de trabalho pendente (RecoveryExerciseType.PendingWorkRebuild).</summary>
    public static async Task<CanaryScenarioResult> ResolveCrashRecoveryAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await store.GetLatestAsync(scope, RecoveryExerciseType.PendingWorkRebuild, cancellationToken).ConfigureAwait(false);
        return MapRecoveryRecord(record, CrashRecoveryId, "CRASH_RECOVERY_NOT_EXERCISED", now);
    }

    /// <summary>CANARY.RESTORE_ROLLBACK_OPERATIONAL — restore drill (RecoveryExerciseType.RestoreDrill); permanece Blocked/NotPerformed sem drill comprovado (escopo obrigatório item 9).</summary>
    public static async Task<CanaryScenarioResult> ResolveRestoreRollbackOperationalAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await store.GetLatestAsync(scope, RecoveryExerciseType.RestoreDrill, cancellationToken).ConfigureAwait(false);
        return MapRecoveryRecord(record, RestoreRollbackOperationalId, "RESTORE_ROLLBACK_NOT_EXERCISED", now);
    }

    /// <summary>
    /// CANARY.RECONCILIATION_EVIDENCE_PACKAGE — o reconciliation certificate canônico e vigente da onda/job
    /// planejado do canário (escopo obrigatório item 10). <see cref="ReconciliationOutcome.Pass"/>/
    /// <see cref="ReconciliationOutcome.PassWithExplainedExceptions"/> apenas — <c>Inconclusive</c>,
    /// <c>Fail</c> e <c>DuplicateRisk</c> nunca são Pass; ausente é NotPerformed (nenhum reconciliation ainda
    /// emitido para esta onda/job de canário).
    /// </summary>
    public static async Task<CanaryScenarioResult> ResolveReconciliationEvidencePackageAsync(
        IReconciliationCertificateStore store, TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var certificate = await store.GetLatestAsync(scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (certificate is null)
        {
            return CanaryScenarioResult.Create(
                ReconciliationEvidencePackageId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None,
                "RECONCILIATION_CERTIFICATE_NOT_ISSUED", now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(
            certificate.CertificateHash, $"reconciliation-certificate:{wave.Value:N}:{plannedJobName.Value}:v{certificate.CertificateVersion.ToString(CultureInfo.InvariantCulture)}");

        return certificate.Result switch
        {
            ReconciliationOutcome.Pass or ReconciliationOutcome.PassWithExplainedExceptions =>
                CanaryScenarioResult.Create(ReconciliationEvidencePackageId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, certificate.GeneratedAtUtc),
            ReconciliationOutcome.Inconclusive =>
                CanaryScenarioResult.Create(ReconciliationEvidencePackageId, CanaryScenarioStatus.Blocked, evidence, "RECONCILIATION_INCONCLUSIVE", certificate.GeneratedAtUtc),
            _ =>
                CanaryScenarioResult.Create(ReconciliationEvidencePackageId, CanaryScenarioStatus.Fail, evidence, $"RECONCILIATION_{certificate.Result.ToString().ToUpperInvariant()}", certificate.GeneratedAtUtc),
        };
    }

    private static CanaryScenarioResult MapRecoveryRecord(
        RecoveryReadinessRecord? record, CanaryScenarioId scenarioId, string missingReasonCode, DateTimeOffset now)
    {
        if (record is null)
        {
            return CanaryScenarioResult.Create(
                scenarioId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None, missingReasonCode, now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(
            record.EvidenceFingerprint, $"recovery-readiness:{record.ExerciseType}:v{record.ExerciseVersion.ToString(CultureInfo.InvariantCulture)}");

        return record.Status switch
        {
            RecoveryReadinessStatus.Pass =>
                CanaryScenarioResult.Create(scenarioId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, record.ExecutedAtUtc),
            RecoveryReadinessStatus.Blocked =>
                CanaryScenarioResult.Create(scenarioId, CanaryScenarioStatus.Blocked, evidence, "RECOVERY_OBJECTIVE_NOT_MET", record.ExecutedAtUtc),
            _ =>
                CanaryScenarioResult.Create(scenarioId, CanaryScenarioStatus.NotPerformed, evidence, missingReasonCode, record.ExecutedAtUtc),
        };
    }
}
