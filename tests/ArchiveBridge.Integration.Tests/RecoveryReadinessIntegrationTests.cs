using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Recovery;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-005 (SQL Server real) — <see cref="RestoreDrillHarness"/> (backup/restore nativo sobre um banco
/// efêmero DEDICADO, nunca produção nem o banco compartilhado da coleção) e
/// <see cref="SqlRecoveryReadinessStore"/>: integridade do estado canônico após restore, tamper-evidence
/// pós-restore, convergência idempotente, RTO medido fail-closed contra o objetivo documentado e isolamento
/// cross-tenant. STOP-THE-LINE: nenhum teste aqui declara HA comprovada nem opera sobre o banco de
/// produção/compartilhado.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class RecoveryReadinessIntegrationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private SqlRecoveryReadinessStore ReadinessStore() => new(fixture.Factory);

    [Fact]
    public async Task ARestoreDrillPreservesCanonicalStateWrittenBeforeTheBackupAndDiscardsStateWrittenAfter()
    {
        await using var harness = await RestoreDrillHarness.CreateAsync(
            Path.Combine(fixture.ArtifactRoot, "dr-drill-" + Guid.NewGuid().ToString("N")), CancellationToken.None);

        var clock = new MutableClock(Start);
        var jobStore = new SqlJobStore(harness.Factory, clock, TimeSpan.FromDays(3650));
        var scope = SqlServerFixture.NewScope();

        // Estado canônico escrito ANTES do backup — deve sobreviver ao restore intacto.
        var preservedJobId = await jobStore.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);
        var beforeBackup = await jobStore.GetAsync(scope, preservedJobId, CancellationToken.None);

        var backupDuration = await harness.BackupAsync(CancellationToken.None);
        Assert.True(backupDuration >= TimeSpan.Zero);

        // Estado escrito DEPOIS do backup — o restore deve descartá-lo (prova de que o restore REALMENTE
        // reverteu o banco ao ponto do backup, não é um no-op).
        var discardedJobId = await jobStore.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);
        Assert.NotNull(await jobStore.GetAsync(scope, discardedJobId, CancellationToken.None));

        var restoreDuration = await harness.RestoreAsync(CancellationToken.None);
        Assert.True(restoreDuration >= TimeSpan.Zero);

        var afterRestore = await jobStore.GetAsync(scope, preservedJobId, CancellationToken.None);
        Assert.NotNull(afterRestore);
        Assert.Equal(beforeBackup!.Id, afterRestore!.Id);
        Assert.Equal(beforeBackup.State, afterRestore.State);
        Assert.Equal(beforeBackup.Tenant, afterRestore.Tenant);
        Assert.Equal(beforeBackup.Project, afterRestore.Project);

        Assert.Null(await jobStore.GetAsync(scope, discardedJobId, CancellationToken.None));

        // O objetivo documentado (Control Plane RTO <= 4h) é medido com a duração REAL observada — nunca
        // alegada — e registrado como evidência executável (item 2/acceptance criteria 1/4).
        var readiness = ReadinessStore();
        var measurement = new RecoveryObjectiveMeasurement(Start, Start + backupDuration + restoreDuration);
        var record = await readiness.RecordExerciseAsync(
            scope, RecoveryExerciseType.RestoreDrill, RecoveryReadinessStatus.Pass, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), measurement, DeterministicHash.Compute([preservedJobId.Value.ToString("N")]),
            failureDomain: string.Empty, notes: "Restore drill sobre banco efêmero dedicado do teste.",
            executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(), Start,
            CancellationToken.None);

        Assert.Equal(RecoveryReadinessStatus.Pass, record.Status);
        Assert.Equal(1, record.ExerciseVersion);
    }

    [Fact]
    public async Task RecordExerciseAsyncConvergesIdempotentlyForAnIdenticalReplayAndNeverDuplicatesTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var readiness = ReadinessStore();
        var measurement = new RecoveryObjectiveMeasurement(Start, Start + TimeSpan.FromMinutes(30));
        var evidence = DeterministicHash.Compute(["replay-test"]);

        // O alvo objetivo precisa comportar a duração medida (30 min) para que Pass seja um resultado
        // válido — RecoveryReadinessRecord.Pass recusa (fail-closed) qualquer medição que exceda o alvo.
        // ControlPlaneRto (não ControlPlaneRpo — AB-I7-007 item 2: RPO nunca é Pass nesta baseline, ver
        // RecoveryReadinessRecordTests.RpoObjectivesCanNeverResultInPassUntilAFailureBoundaryDrillExists).
        Task<RecoveryReadinessRecord> Record() => readiness.RecordExerciseAsync(
            scope, RecoveryExerciseType.PendingWorkRebuild, RecoveryReadinessStatus.Pass, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(1), measurement, evidence, failureDomain: string.Empty, notes: "rebuild ok.",
            executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(), Start, CancellationToken.None);

        var first = await Record();
        var second = await Record();

        Assert.Equal(first.ExerciseVersion, second.ExerciseVersion);
        Assert.Equal(first.RecordHash, second.RecordHash);

        var history = await readiness.GetHistoryAsync(scope, RecoveryExerciseType.PendingWorkRebuild, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task ADifferentResultForTheSameExerciseTypeProducesANewVersionInsteadOfOverwriting()
    {
        var scope = SqlServerFixture.NewScope();
        var readiness = ReadinessStore();
        var evidence = DeterministicHash.Compute(["version-test"]);

        var first = await readiness.RecordExerciseAsync(
            scope, RecoveryExerciseType.ArtifactEvidenceRecovery, RecoveryReadinessStatus.NotMeasured, RecoveryObjective.None,
            objectiveThreshold: null, measurement: null, evidence, failureDomain: string.Empty, notes: "ainda não executado.",
            executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(), Start, CancellationToken.None);

        var measurement = new RecoveryObjectiveMeasurement(Start, Start + TimeSpan.FromMinutes(2));
        var second = await readiness.RecordExerciseAsync(
            scope, RecoveryExerciseType.ArtifactEvidenceRecovery, RecoveryReadinessStatus.Pass, RecoveryObjective.None,
            objectiveThreshold: null, measurement, evidence, failureDomain: string.Empty, notes: "exercitado com sucesso.",
            executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(), Start.AddMinutes(10),
            CancellationToken.None);

        Assert.Equal(1, first.ExerciseVersion);
        Assert.Equal(2, second.ExerciseVersion);
        Assert.NotEqual(first.RecordHash, second.RecordHash);

        var latest = await readiness.GetLatestAsync(scope, RecoveryExerciseType.ArtifactEvidenceRecovery, CancellationToken.None);
        Assert.Equal(RecoveryReadinessStatus.Pass, latest!.Status);

        var history = await readiness.GetHistoryAsync(scope, RecoveryExerciseType.ArtifactEvidenceRecovery, CancellationToken.None);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task ARecordTamperedDirectlyInTheDatabaseIsRejectedFailClosedOnReadNeverSilentlyReturned()
    {
        var scope = SqlServerFixture.NewScope();
        var readiness = ReadinessStore();
        var measurement = new RecoveryObjectiveMeasurement(Start, Start + TimeSpan.FromMinutes(1));

        await readiness.RecordExerciseAsync(
            scope, RecoveryExerciseType.RestoreDrill, RecoveryReadinessStatus.Pass, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), measurement, DeterministicHash.Compute(["tamper-test"]), failureDomain: string.Empty,
            notes: "ok", executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(), Start,
            CancellationToken.None);

        // Adulteração direta no banco (fora do caminho de escrita da store) — simula corrupção/tampering
        // detectado após um restore real (item 7 do work order).
        await using (var connection = new SqlConnection(fixture.AdminConnectionString))
        {
            await connection.OpenAsync();

            // dbo.recovery_readiness_evidence tem FILTER PREDICATE de RLS por SESSION_CONTEXT('tenant_id')
            // (migration 0040): sem esse contexto definido, a linha do tenant do escopo fica invisível
            // para esta conexão crua e o UPDATE abaixo afetaria silenciosamente zero linhas (nenhum erro,
            // nenhuma adulteração real) — mesmo padrão de
            // ReconciliationCertificateIntegrationTests.ExecuteAdminSqlAsync.
            await using (var context = new SqlCommand(
                "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
            {
                context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                await context.ExecuteNonQueryAsync();
            }

            await using var command = new SqlCommand(
                "UPDATE dbo.recovery_readiness_evidence SET notes = 'ADULTERADO' WHERE tenant_id = @tenant AND project_id = @project AND exercise_type = 0;",
                connection);
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            var rowsUpdated = await command.ExecuteNonQueryAsync();
            Assert.Equal(1, rowsUpdated);
        }

        await Assert.ThrowsAsync<RecoveryReadinessIntegrityViolationException>(
            () => readiness.GetLatestAsync(scope, RecoveryExerciseType.RestoreDrill, CancellationToken.None));
    }

    [Fact]
    public async Task ARecoveryReadinessRecordFromOneTenantIsInvisibleToAnotherTenantsScope()
    {
        var readiness = ReadinessStore();
        var tenantAScope = SqlServerFixture.NewScope();
        var tenantBScope = SqlServerFixture.NewScope();

        await readiness.RecordExerciseAsync(
            tenantAScope, RecoveryExerciseType.HaFailover, RecoveryReadinessStatus.Blocked, RecoveryObjective.None,
            objectiveThreshold: null, measurement: null, RecoveryReadinessRecord.NoEvidenceFingerprint,
            failureDomain: "Segredo protegido por DPAPI single-node — sem failover comprovado.", notes: string.Empty,
            executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(), Start, CancellationToken.None);

        var fromTenantB = await readiness.GetLatestAsync(tenantBScope, RecoveryExerciseType.HaFailover, CancellationToken.None);

        Assert.Null(fromTenantB);
    }

    [Fact]
    public async Task TheDatabaseItselfRejectsAnHaFailoverRowMarkedPassAsDefenseInDepthBeyondTheDomainGuard()
    {
        var scope = SqlServerFixture.NewScope();

        // Seed do FK obrigatório (dbo.projects) — mesmo padrão usado por SqlJobStore.CreateSql.
        await using (var connection = new SqlConnection(fixture.AdminConnectionString))
        {
            await connection.OpenAsync();

            // dbo.projects e dbo.recovery_readiness_evidence têm BLOCK PREDICATE de RLS por
            // SESSION_CONTEXT('tenant_id') (migrations 0003/0040): uma conexão crua sem esse
            // contexto definido é bloqueada mesmo para o tenant do próprio escopo do teste — mesmo
            // padrão já usado por ReconciliationCertificateIntegrationTests.ExecuteAdminSqlAsync.
            await using (var context = new SqlCommand(
                "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
            {
                context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                await context.ExecuteNonQueryAsync();
            }

            await using var seedProject = new SqlCommand(
                "IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE project_id = @project) " +
                "INSERT INTO dbo.projects (project_id, tenant_id) VALUES (@project, @tenant);", connection);
            seedProject.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            seedProject.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await seedProject.ExecuteNonQueryAsync();

            await using var command = new SqlCommand(
                """
                INSERT INTO dbo.recovery_readiness_evidence
                    (tenant_id, project_id, exercise_type, exercise_version, status, objective, objective_threshold_ticks,
                     measurement_started_at_utc, measurement_completed_at_utc, evidence_fingerprint, failure_domain, notes,
                     exercise_fingerprint, executed_by, executed_by_role, correlation_id, executed_at_utc, schema_version, record_hash)
                VALUES
                    (@tenant, @project, 3, 1, 2, 0, NULL, SYSUTCDATETIME(), SYSUTCDATETIME(), REPLICATE('a', 64),
                     N'', N'', REPLICATE('b', 64), N'attacker', N'ServiceAccount', NEWID(), SYSUTCDATETIME(),
                     N'test', REPLICATE('c', 64));
                """,
                connection);
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

            var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("CK_rre_ha_never_pass", exception.Message, StringComparison.Ordinal);
        }
    }
}
