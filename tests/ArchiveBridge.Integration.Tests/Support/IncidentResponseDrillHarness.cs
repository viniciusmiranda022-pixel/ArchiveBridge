using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Infrastructure.Security;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>
/// Harness de incident-response sintético e NÃO DESTRUTIVO (AB-I7-008 item 5) — mesmo espírito de
/// <see cref="RestoreDrillHarness"/>: exercita mecanismos REAIS (redação, integridade, RLS) sobre o banco
/// de teste dedicado de <see cref="SqlServerFixture"/>, nunca sobre produção, e nunca com efeito externo.
/// Cada método executa UM drill e persiste a evidência via <see cref="SqlIncidentResponseDrillStore"/> —
/// nenhum drill armazena o segredo/PII canário, apenas o digest e uma disposition operacional segura.
/// </summary>
public sealed class IncidentResponseDrillHarness(SqlServerFixture fixture)
{
    private const string TenantScopeIdForRedaction = "incident-response-drill-harness";

    private SqlIncidentResponseDrillStore Store() => new(fixture.Factory);

    /// <summary>
    /// Drill 1 — Secret-leak canary: injeta um segredo canário SINTÉTICO através de
    /// <see cref="SecretRedactor.Redact"/> e persiste evidência de que o valor bruto NÃO sobreviveu.
    /// </summary>
    public async Task<IncidentResponseDrillRecord> RunSecretLeakCanaryAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const string canarySecret = "Authorization: Bearer canary-drill-secret-DO-NOT-PERSIST-999";
        var startedAt = now;

        var redacted = SecretRedactor.Redact(canarySecret, TenantScopeIdForRedaction);
        var contained = !redacted.Contains("canary-drill-secret-DO-NOT-PERSIST-999", StringComparison.Ordinal);

        var evidenceDigest = DeterministicHash.Compute(["incident-response-drill.secret-leak-canary.v1", redacted]);
        var disposition = contained
            ? "Canary secret was redacted by SecretRedactor before any persistence; raw value never stored."
            : "Canary secret SURVIVED redaction — real defect, escalate immediately.";

        return await Store().RecordDrillAsync(
            scope, IncidentResponseDrillType.SecretLeakCanary, contained ? IncidentResponseDrillOutcome.Contained : IncidentResponseDrillOutcome.Failed,
            startedAt, now.AddMilliseconds(1), evidenceDigest, disposition, executedBy: "incident-response-drill-harness",
            executedByRole: "ServiceAccount", CorrelationId.New(), now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drill 2 — Hash-mismatch/tampering: persiste um registro de evidência REAL, adultera-o fora do
    /// caminho de escrita (mesma técnica de <c>RecoveryReadinessIntegrationTests</c>) e verifica que a
    /// revalidação de integridade lança fail-closed.
    /// </summary>
    public async Task<IncidentResponseDrillRecord> RunHashMismatchTamperingAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var startedAt = now;
        var store = new SqlWorkerHardeningBaselineStore(fixture.Factory);
        await store.RecordControlAsync(
            scope, WorkerHardeningControl.BitLocker, WorkerHardeningStatus.NotMeasured, measurement: null,
            WorkerHardeningControlRecord.NoEvidenceFingerprint, blockedReason: string.Empty, notes: "seed record for tampering drill.",
            executedBy: "incident-response-drill-harness", executedByRole: "ServiceAccount", CorrelationId.New(), now,
            cancellationToken).ConfigureAwait(false);

        await using (var connection = new SqlConnection(fixture.AdminConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var context = new SqlCommand(
                "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
            {
                context.Parameters.Add(new SqlParameter("@tenant", System.Data.SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                await context.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var tamper = new SqlCommand(
                "UPDATE dbo.security_worker_hardening_evidence SET notes = 'ADULTERADO' " +
                "WHERE tenant_id = @tenant AND project_id = @project AND control = 4;",
                connection);
            tamper.Parameters.Add(new SqlParameter("@tenant", System.Data.SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            tamper.Parameters.Add(new SqlParameter("@project", System.Data.SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await tamper.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var integrityViolationDetected = false;
        try
        {
            await store.GetLatestAsync(scope, WorkerHardeningControl.BitLocker, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerHardeningIntegrityViolationException)
        {
            integrityViolationDetected = true;
        }

        var evidenceDigest = DeterministicHash.Compute(["incident-response-drill.hash-mismatch-tampering.v1", scope.Tenant.Value.ToString("N")]);
        var disposition = integrityViolationDetected
            ? "Tampered evidence row was rejected fail-closed on read (RecordHash revalidation)."
            : "Tampering was NOT detected — real defect, escalate immediately.";

        return await Store().RecordDrillAsync(
            scope, IncidentResponseDrillType.HashMismatchTampering,
            integrityViolationDetected ? IncidentResponseDrillOutcome.Contained : IncidentResponseDrillOutcome.Failed,
            startedAt, now.AddMilliseconds(1), evidenceDigest, disposition, executedBy: "incident-response-drill-harness",
            executedByRole: "ServiceAccount", CorrelationId.New(), now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drill 3 — Cross-tenant denial: tenta ler a evidência de OUTRO tenant através do escopo do tenant do
    /// drill e verifica que a RLS a torna invisível (nunca uma exceção que revele existência).
    /// </summary>
    public async Task<IncidentResponseDrillRecord> RunCrossTenantDenialAsync(
        TenantScope scope, TenantScope otherTenantScope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var startedAt = now;
        var store = new SqlWorkerHardeningBaselineStore(fixture.Factory);
        await store.RecordControlAsync(
            otherTenantScope, WorkerHardeningControl.CrashDumpHandling, WorkerHardeningStatus.NotMeasured, measurement: null,
            WorkerHardeningControlRecord.NoEvidenceFingerprint, blockedReason: string.Empty, notes: "belongs to the other tenant.",
            executedBy: "incident-response-drill-harness", executedByRole: "ServiceAccount", CorrelationId.New(), now,
            cancellationToken).ConfigureAwait(false);

        var visibleFromDrillTenant = await store.GetLatestAsync(scope, WorkerHardeningControl.CrashDumpHandling, cancellationToken).ConfigureAwait(false);
        var denied = visibleFromDrillTenant is null;

        var evidenceDigest = DeterministicHash.Compute(["incident-response-drill.cross-tenant-denial.v1", scope.Tenant.Value.ToString("N")]);
        var disposition = denied
            ? "Cross-tenant read was denied by RLS (rls.tenant_isolation_policy) — the other tenant's row is invisible."
            : "Cross-tenant read was NOT denied — real defect, escalate immediately.";

        return await Store().RecordDrillAsync(
            scope, IncidentResponseDrillType.CrossTenantDenial, denied ? IncidentResponseDrillOutcome.Contained : IncidentResponseDrillOutcome.Failed,
            startedAt, now.AddMilliseconds(1), evidenceDigest, disposition, executedBy: "incident-response-drill-harness",
            executedByRole: "ServiceAccount", CorrelationId.New(), now, cancellationToken).ConfigureAwait(false);
    }
}
