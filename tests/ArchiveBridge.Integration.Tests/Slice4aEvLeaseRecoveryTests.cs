using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A (sub-incremento 2B) — CRASH RECOVERY do workload EnterpriseVault contra SQL Server real. Prova
/// que um lease expirado após uma queda de worker é recuperado (RetryScheduled/Failed) e pode ser
/// reivindicado de novo com época maior; que o reaper EV NÃO toca Jobs de outros workloads (isolamento); e
/// que um lease AINDA VÁLIDO não é recuperado (proteção da corrida com heartbeat, no caminho escopado).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aEvLeaseRecoveryTests(SqlServerFixture fixture)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private readonly SqlServerFixture _fixture = fixture;

    [Fact]
    public async Task ExpiredEnterpriseVaultLeaseIsRecoveredAndRequeued()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock);
        var leaseManager = _fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var scope = SqlServerFixture.NewScope();

        await inbox.EnqueueAsync(BuildCommand(scope), CancellationToken.None);
        var claimed = await inbox.TryClaimNextAsync(scope, new WorkerId("ev-A"), Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);
        var jobId = claimed!.Job.JobId.Value;

        clock.Advance(Lease + TimeSpan.FromMinutes(1)); // queda do worker: lease expira
        var recovered = await leaseManager.RecoverExpiredLeasesAsync(Workload.EnterpriseVault, 64, CancellationToken.None);

        Assert.True(recovered >= 1);
        var job = await ReadJobAsync(scope, jobId);
        Assert.Equal(2, job.State);          // RetryScheduled (há tentativas restantes)
        Assert.Null(job.OwnerWorker);        // owner limpo
        Assert.Null(job.LeaseExpires);       // lease limpo
        Assert.True(await HasTransitionAsync(scope, jobId, reason: 6)); // LeaseExpiredRecovered
    }

    [Fact]
    public async Task RecoveredEvJobCanBeClaimedAgainWithNewEpoch()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock);
        var leaseManager = _fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var scope = SqlServerFixture.NewScope();

        await inbox.EnqueueAsync(BuildCommand(scope), CancellationToken.None);
        var workerA = new WorkerId("ev-A");
        var first = await inbox.TryClaimNextAsync(scope, workerA, Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(first);
        var epochA = first!.Job.Epoch;

        clock.Advance(Lease + TimeSpan.FromMinutes(1));
        await leaseManager.RecoverExpiredLeasesAsync(Workload.EnterpriseVault, 64, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(30)); // ultrapassa next_attempt
        var second = await inbox.TryClaimNextAsync(scope, new WorkerId("ev-B"), Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(second);
        Assert.True(second!.Job.Epoch.Value > epochA.Value); // época nova

        // Worker A (época antiga) não consegue mais renovar/agir: FencedOut.
        var renew = await leaseManager.RenewAsync(
            new LeaseCommand(scope, first.Job.JobId, workerA, epochA, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(JobCommandOutcome.FencedOut, renew);
    }

    [Fact]
    public async Task EvLeaseRecoveryDoesNotRecoverOtherWorkloads()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock);
        var leaseManager = _fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var store = _fixture.Store(clock);
        var worker = new WorkerId("worker");

        // Job EnterpriseVault: enfileira e reivindica ⇒ Processing.
        var evScope = SqlServerFixture.NewScope();
        await inbox.EnqueueAsync(BuildCommand(evScope), CancellationToken.None);
        var evClaim = await inbox.TryClaimNextAsync(evScope, worker, Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(evClaim);

        // Job de OUTRO workload (Pst): cria e reivindica ⇒ Processing.
        var pstScope = SqlServerFixture.NewScope();
        var pstJob = await store.CreateAsync(
            new CreateJobCommand(pstScope, Workload.Pst, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);
        var pstClaim = await store.TryClaimNextAsync(
            new ClaimRequest(pstScope, Workload.Pst, worker, Lease, CorrelationId.New()), CancellationToken.None);
        Assert.NotNull(pstClaim);

        clock.Advance(Lease + TimeSpan.FromMinutes(1)); // ambos os leases expiram
        var recovered = await leaseManager.RecoverExpiredLeasesAsync(Workload.EnterpriseVault, 64, CancellationToken.None);
        Assert.True(recovered >= 1);

        // O Job EV foi recuperado; o de OUTRO workload continua Processing (não tocado pelo reaper EV).
        Assert.Equal(2, (await ReadJobAsync(evScope, evClaim!.Job.JobId.Value)).State);   // RetryScheduled
        var pst = await ReadJobAsync(pstScope, pstJob.Value);
        Assert.Equal(1, pst.State);                 // ainda Processing
        Assert.NotNull(pst.OwnerWorker);            // owner preservado
    }

    [Fact]
    public async Task ExpiredEvLeaseFailsWhenAttemptsAreExhausted()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock);
        // Política sem tentativas restantes após o primeiro claim (MaxAttempts = 1).
        var exhausting = new RetryPolicy(1, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
        var leaseManager = _fixture.LeaseManager(clock, exhausting, Lease);
        var scope = SqlServerFixture.NewScope();

        await inbox.EnqueueAsync(BuildCommand(scope), CancellationToken.None);
        var claimed = await inbox.TryClaimNextAsync(scope, new WorkerId("ev-A"), Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);

        clock.Advance(Lease + TimeSpan.FromMinutes(1));
        await leaseManager.RecoverExpiredLeasesAsync(Workload.EnterpriseVault, 64, CancellationToken.None);

        var job = await ReadJobAsync(scope, claimed!.Job.JobId.Value);
        Assert.Equal(4, job.State);          // Failed (tentativas esgotadas), NÃO RetryScheduled
        Assert.True(await HasTransitionAsync(scope, claimed.Job.JobId.Value, reason: 7)); // AttemptsExhausted
    }

    [Fact]
    public async Task ScopedReaperDoesNotRecoverAnUnexpiredLease()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock);
        var leaseManager = _fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var scope = SqlServerFixture.NewScope();

        await inbox.EnqueueAsync(BuildCommand(scope), CancellationToken.None);
        var claimed = await inbox.TryClaimNextAsync(scope, new WorkerId("ev-A"), Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);

        // NÃO avança o relógio: o lease continua VÁLIDO (equivale a um heartbeat que renovou antes do UPDATE).
        await leaseManager.RecoverExpiredLeasesAsync(Workload.EnterpriseVault, 64, CancellationToken.None);

        var job = await ReadJobAsync(scope, claimed!.Job.JobId.Value);
        Assert.Equal(1, job.State);       // continua Processing (lease válido não é recuperado)
        Assert.NotNull(job.OwnerWorker);  // owner preservado
        Assert.NotNull(job.LeaseExpires); // lease preservado
    }

    private static EvDiscoveryCommand BuildCommand(TenantScope scope) =>
        new(
            scope,
            new EvEnvironmentId(Guid.NewGuid()),
            "site-a",
            "dir-a.contoso.local",
            "operator@contoso.local",
            CorrelationId.New(),
            new EvDiscoveryCommandContext(
                EvDiscoveryCommandContext.CurrentSchemaVersion, 1, new Sha256Hash(new string('a', 64)),
                EvDiscoveryPolicy.Default.PolicyVersion));

    private async Task<JobRow> ReadJobAsync(TenantScope scope, Guid jobId)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT state, owner_worker, lease_expires_at_utc, attempt_count, lease_epoch FROM dbo.jobs WHERE job_id = @job AND project_id = @project;",
            tenant.Connection);
        command.Parameters.Add(new SqlParameter("@job", SqlDbType.UniqueIdentifier) { Value = jobId });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new JobRow(
            reader.GetByte(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.GetInt32(3),
            reader.GetInt64(4));
    }

    private async Task<bool> HasTransitionAsync(TenantScope scope, Guid jobId, byte reason)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.job_state_transitions WHERE job_id = @job AND project_id = @project AND reason_code = @reason;",
            tenant.Connection);
        command.Parameters.Add(new SqlParameter("@job", SqlDbType.UniqueIdentifier) { Value = jobId });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.TinyInt) { Value = reason });
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        return count > 0;
    }

    private sealed record JobRow(byte State, string? OwnerWorker, DateTime? LeaseExpires, int AttemptCount, long LeaseEpoch);
}
