using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Operations;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ProjectIsolationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static (TenantScope A, TenantScope B) SameTenantTwoProjects()
    {
        var tenant = new TenantId(Guid.NewGuid());
        return (new TenantScope(tenant, new ProjectId(Guid.NewGuid())),
                new TenantScope(tenant, new ProjectId(Guid.NewGuid())));
    }

    [Fact]
    public async Task ProjectBCannotReadClaimRenewCompleteFailOrRetryProjectAJob()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var audit = fixture.AuditReader();
        var (projectA, projectB) = SameTenantTwoProjects();
        var worker = new WorkerId("w");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(projectA, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(projectA, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var leaseFromB = new LeaseCommand(projectB, jobId, worker, claimed!.Epoch, CorrelationId.New());

        // Mesmo tenant, projeto diferente: o filtro por project_id (não a RLS) barra o acesso.
        Assert.Null(await store.GetAsync(projectB, jobId, CancellationToken.None));
        Assert.Null(await store.TryClaimNextAsync(
            new ClaimRequest(projectB, Workload.Pst, worker, Lease, CorrelationId.New()), CancellationToken.None));
        Assert.Equal(JobCommandOutcome.NotFound, await leases.RenewAsync(leaseFromB, CancellationToken.None));
        Assert.Equal(JobCommandOutcome.NotFound, await store.CompleteAsync(leaseFromB, CancellationToken.None));
        Assert.Equal(JobCommandOutcome.NotFound, await store.FailAsync(leaseFromB, ErrorCode.PermanentProvider, CancellationToken.None));
        Assert.Equal(JobCommandOutcome.NotFound,
            await store.ScheduleRetryAsync(leaseFromB, ErrorCode.TransientProvider, clock.UtcNow.AddMinutes(1), CancellationToken.None));
        Assert.Empty(await audit.GetTransitionsAsync(projectB, jobId, CancellationToken.None));

        // O Job de A permanece intacto (Processing).
        var job = await store.GetAsync(projectA, jobId, CancellationToken.None);
        Assert.Equal(JobState.Processing, job!.State);
    }

    [Fact]
    public async Task AssociatingAJobToAnotherTenantsProjectIsRejectedAndRolledBack()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var tenantB = SqlServerFixture.NewScope();

        // Tenant B cria (e passa a possuir) o projeto.
        await store.CreateAsync(
            new CreateJobCommand(tenantB, Workload.Upload, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        // Tenant A tenta criar um Job no MESMO project_id (de B): rejeitado no banco (PK/FK compostas).
        var tenantAWithBProject = new TenantScope(new TenantId(Guid.NewGuid()), tenantB.Project);
        await Assert.ThrowsAsync<SqlException>(() => store.CreateAsync(
            new CreateJobCommand(tenantAWithBProject, Workload.Upload, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None));

        // Rollback: nada foi persistido para A nesse projeto.
        var singleTenantA = new TenantScope(tenantAWithBProject.Tenant, tenantB.Project);
        Assert.Null(await store.TryClaimNextAsync(
            new ClaimRequest(singleTenantA, Workload.Upload, new WorkerId("a"), Lease, CorrelationId.New()),
            CancellationToken.None));
    }
}
