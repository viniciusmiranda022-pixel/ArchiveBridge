using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Infrastructure.Security;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-008 (SQL Server real) — as cinco stores novas de evidência de segurança (worker hardening
/// baseline, WDAC policy, supply-chain build provenance, incident-response drills, pen-test readiness):
/// convergência idempotente, tamper-evidence via revalidação de hash, e as DUAS demonstrações exigidas
/// pelo work order contra as tabelas NOVAS deste Passo — cross-tenant denial (RLS) e privilege spoofing
/// (defesa em profundidade no schema: nenhum papel/ator, mesmo via INSERT direto, consegue persistir um
/// resultado estruturalmente proibido).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SecurityHardeningEvidenceIntegrationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly CorrelationId Correlation = CorrelationId.New();

    private SqlWorkerHardeningBaselineStore WorkerHardeningStore() => new(fixture.Factory);

    private SqlWdacPolicyEvidenceStore WdacStore() => new(fixture.Factory);

    private SqlBuildProvenanceStore BuildProvenanceStore() => new(fixture.Factory);

    private SqlIncidentResponseDrillStore IncidentResponseStore() => new(fixture.Factory);

    private SqlPenTestReadinessStore PenTestReadinessStore() => new(fixture.Factory);

    [Fact]
    public async Task WorkerHardeningRecordControlAsyncConvergesIdempotentlyAndTamperedRowsFailClosedOnRead()
    {
        var scope = SqlServerFixture.NewScope();
        var store = WorkerHardeningStore();
        var measurement = new WorkerHardeningMeasurement(Start, "local policy query via WMI");
        var evidence = DeterministicHash.Compute(["worker-hardening-integration-test"]);

        Task<WorkerHardeningControlRecord> Record() => store.RecordControlAsync(
            scope, WorkerHardeningControl.BitLocker, WorkerHardeningStatus.Pass, measurement, evidence,
            blockedReason: string.Empty, notes: "BitLocker enabled.", executedBy: "integration-tests",
            executedByRole: "ServiceAccount", Correlation, Start, CancellationToken.None);

        var first = await Record();
        var second = await Record();

        Assert.Equal(first.ControlVersion, second.ControlVersion);
        Assert.Equal(first.RecordHash, second.RecordHash);

        await using (var connection = new SqlConnection(fixture.AdminConnectionString))
        {
            await connection.OpenAsync();
            await SetSessionTenantAsync(connection, scope.Tenant.Value);
            await using var tamper = new SqlCommand(
                "UPDATE dbo.security_worker_hardening_evidence SET notes = 'ADULTERADO' " +
                "WHERE tenant_id = @tenant AND project_id = @project AND control = 4;", connection);
            tamper.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            tamper.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<WorkerHardeningIntegrityViolationException>(
            () => store.GetLatestAsync(scope, WorkerHardeningControl.BitLocker, CancellationToken.None));
    }

    [Fact]
    public async Task WdacPolicyRecordPolicyAsyncConvergesIdempotentlyAndValidatesAKnownEntry()
    {
        var scope = SqlServerFixture.NewScope();
        var store = WdacStore();
        var workerHash = new Sha256Hash(new string('c', 64));
        var entries = new[] { WdacAllowlistEntry.Create(publisher: null, workerHash, pathRule: null) };

        Task<WdacPolicyEvidence> Record() => store.RecordPolicyAsync(
            scope, entries, issuedBy: "integration-tests", issuedByRole: "ServiceAccount", Correlation, Start, CancellationToken.None);

        var first = await Record();
        var second = await Record();

        Assert.Equal(first.PolicyVersion, second.PolicyVersion);

        var latest = await store.GetLatestAsync(scope, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(WdacValidationOutcome.Allowed, latest!.Validate(new WdacCandidateBinary(Publisher: null, workerHash, Path: null)));
        Assert.Equal(WdacValidationOutcome.Denied, latest.Validate(new WdacCandidateBinary(Publisher: null, new Sha256Hash(new string('d', 64)), Path: null)));
    }

    [Fact]
    public async Task WdacPolicyTamperedEntriesFailClosedOnRead()
    {
        var scope = SqlServerFixture.NewScope();
        var store = WdacStore();
        var workerHash = new Sha256Hash(new string('c', 64));
        var entries = new[] { WdacAllowlistEntry.Create(publisher: null, workerHash, pathRule: null) };
        await store.RecordPolicyAsync(scope, entries, "integration-tests", "ServiceAccount", Correlation, Start, CancellationToken.None);

        await using (var connection = new SqlConnection(fixture.AdminConnectionString))
        {
            await connection.OpenAsync();
            await SetSessionTenantAsync(connection, scope.Tenant.Value);
            await using var tamper = new SqlCommand(
                "UPDATE dbo.security_wdac_policy_evidence SET entries_canonical = 'tampered' " +
                "WHERE tenant_id = @tenant AND project_id = @project;", connection);
            tamper.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            tamper.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<WdacPolicyIntegrityViolationException>(() => store.GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task BuildProvenanceApproveAsyncConvergesAndPromotionVerifierFailsClosedOnDrift()
    {
        var scope = SqlServerFixture.NewScope();
        var store = BuildProvenanceStore();
        var approvedDigest = new Sha256Hash(new string('a', 64));
        const string commitSha = "abc1234567890def1234567890abcdef12345678";

        Task<BuildProvenanceRecord> Approve() => store.ApproveAsync(
            scope, "ArchiveBridge.Workers.Upload", commitSha, "github-actions-runner", Start, approvedDigest,
            "integration-tests", "ServiceAccount", Correlation, Start, CancellationToken.None);

        var first = await Approve();
        var second = await Approve();
        Assert.Equal(first.ArtifactVersion, second.ArtifactVersion);

        var latest = await store.GetLatestAsync(scope, "ArchiveBridge.Workers.Upload", CancellationToken.None);
        Assert.NotNull(latest);

        ArtifactPromotionVerifier.VerifyPromotion(latest!, approvedDigest);

        var driftedDigest = new Sha256Hash(new string('b', 64));
        Assert.Throws<SupplyChainPromotionDriftException>(() => ArtifactPromotionVerifier.VerifyPromotion(latest!, driftedDigest));
    }

    [Fact]
    public async Task IncidentResponseDrillRecordDrillAsyncConvergesIdempotentlyAndIsInvisibleAcrossTenants()
    {
        var tenantAScope = SqlServerFixture.NewScope();
        var tenantBScope = SqlServerFixture.NewScope();
        var store = IncidentResponseStore();
        var evidenceDigest = DeterministicHash.Compute(["incident-response-integration-test"]);

        Task<IncidentResponseDrillRecord> Record() => store.RecordDrillAsync(
            tenantAScope, IncidentResponseDrillType.SecretLeakCanary, IncidentResponseDrillOutcome.Contained, Start,
            Start.AddSeconds(1), evidenceDigest, "Canary secret redacted before persistence.", "integration-tests",
            "ServiceAccount", Correlation, Start, CancellationToken.None);

        var first = await Record();
        var second = await Record();
        Assert.Equal(first.DrillVersion, second.DrillVersion);

        // Cross-tenant denial (acceptance criteria 5): a evidência do tenant A é invisível para o tenant B.
        var fromTenantB = await store.GetLatestAsync(tenantBScope, IncidentResponseDrillType.SecretLeakCanary, CancellationToken.None);
        Assert.Null(fromTenantB);
    }

    [Fact]
    public async Task PenTestReadinessRecordBundleAsyncConvergesIdempotently()
    {
        var scope = SqlServerFixture.NewScope();
        var store = PenTestReadinessStore();
        var targetDigest = new Sha256Hash(new string('a', 64));

        Task<PenTestReadinessBundle> Record() => store.RecordBundleAsync(
            scope, PenTestReadinessStatus.NotPerformed, "Control plane and worker fleet.", "Public portal endpoints.",
            "Tenant boundary via RLS.", "Synthetic fixtures only.", "No independent report exists yet.", targetDigest,
            blockedReason: string.Empty, preparedBy: "integration-tests", preparedByRole: "ServiceAccount", Correlation,
            Start, CancellationToken.None);

        var first = await Record();
        var second = await Record();

        Assert.Equal(first.BundleVersion, second.BundleVersion);
        Assert.Equal(PenTestReadinessStatus.NotPerformed, second.Status);
    }

    // Privilege spoofing (acceptance criteria 5): mesmo um INSERT direto no banco — como se um ator com um
    // papel elevado tentasse forjar um resultado estruturalmente proibido, contornando toda a aplicação —
    // é rejeitado pelo PRÓPRIO SCHEMA (defesa em profundidade além do tipo Domain, que já não expõe caso
    // Pass/"concluído" algum para PenTestReadinessStatus).
    [Fact]
    public async Task TheDatabaseItselfRejectsAPenTestReadinessRowClaimingACompletedPassStatus()
    {
        var scope = SqlServerFixture.NewScope();
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await SetSessionTenantAsync(connection, scope.Tenant.Value);
        await SeedProjectAsync(connection, scope);

        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.security_pentest_readiness_bundles
                (tenant_id, project_id, bundle_version, status, scope_summary, attack_surface_summary,
                 trust_boundaries_summary, synthetic_fixtures_description, known_blocked_items_summary,
                 target_build_digest, blocked_reason, content_fingerprint, prepared_by, prepared_by_role,
                 correlation_id, prepared_at_utc, schema_version, record_hash)
            VALUES
                (@tenant, @project, 1, 2, N'x', N'x', N'x', N'x', N'x', REPLICATE('a', 64), N'', REPLICATE('b', 64),
                 N'attacker', N'Administrator', NEWID(), SYSUTCDATETIME(), N'test', REPLICATE('c', 64));
            """,
            connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("CK_prb_status_never_pass", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDatabaseItselfRejectsAWorkerHardeningRowClaimingAnUnsupportedControlPassed()
    {
        var scope = SqlServerFixture.NewScope();
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await SetSessionTenantAsync(connection, scope.Tenant.Value);
        await SeedProjectAsync(connection, scope);

        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.security_worker_hardening_evidence
                (tenant_id, project_id, control, control_version, status, measurement_measured_at_utc,
                 measurement_method, evidence_fingerprint, blocked_reason, notes, content_fingerprint,
                 executed_by, executed_by_role, correlation_id, executed_at_utc, schema_version, record_hash)
            VALUES
                (@tenant, @project, 11, 1, 2, SYSUTCDATETIME(), N'claimed tenant policy query', REPLICATE('a', 64),
                 N'', N'', REPLICATE('b', 64), N'attacker', N'Administrator', NEWID(), SYSUTCDATETIME(), N'test',
                 REPLICATE('c', 64));
            """,
            connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("CK_whe_mde_never_pass", exception.Message, StringComparison.Ordinal);
    }

    private static async Task SetSessionTenantAsync(SqlConnection connection, Guid tenantId)
    {
        await using var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection);
        context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenantId });
        await context.ExecuteNonQueryAsync();
    }

    private static async Task SeedProjectAsync(SqlConnection connection, TenantScope scope)
    {
        await using var seedProject = new SqlCommand(
            "IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE project_id = @project) " +
            "INSERT INTO dbo.projects (project_id, tenant_id) VALUES (@project, @tenant);", connection);
        seedProject.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        seedProject.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        await seedProject.ExecuteNonQueryAsync();
    }
}
