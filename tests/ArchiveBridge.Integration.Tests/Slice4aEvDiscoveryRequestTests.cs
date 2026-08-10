using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A — backbone do disparo idempotente de descoberta EV contra SQL Server real. Prova: enqueue
/// idempotente (mesma chave ⇒ mesmo Job, exatamente 1 Job/1 comando), conflito determinístico para a mesma
/// chave com comando diferente, unicidade sob CONCORRÊNCIA, e o catálogo de ambientes escopado (anti-IDOR).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aEvDiscoveryRequestTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private readonly SqlServerFixture _fixture = fixture;

    [Fact]
    public async Task IdempotentEnqueueCreatesOnceAndReplays()
    {
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, Clock);
        var scope = SqlServerFixture.NewScope();
        var key = Guid.NewGuid();
        var command = BuildCommand(scope, Guid.NewGuid());

        var first = await inbox.EnqueueIdempotentAsync(command, key, CancellationToken.None);
        var second = await inbox.EnqueueIdempotentAsync(command, key, CancellationToken.None);

        Assert.True(first is { Created: true, Replayed: false });
        Assert.True(second is { Created: false, Replayed: true });
        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(1, await CountJobsAsync(scope));
        Assert.Equal(1, await CountCommandsAsync(scope, key));
    }

    [Fact]
    public async Task SameKeyDifferentCommandConflictsWithoutNewJob()
    {
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, Clock);
        var scope = SqlServerFixture.NewScope();
        var key = Guid.NewGuid();

        await inbox.EnqueueIdempotentAsync(BuildCommand(scope, Guid.NewGuid()), key, CancellationToken.None);
        await Assert.ThrowsAsync<EvDiscoveryIdempotencyConflictException>(
            () => inbox.EnqueueIdempotentAsync(BuildCommand(scope, Guid.NewGuid()), key, CancellationToken.None));

        Assert.Equal(1, await CountJobsAsync(scope)); // o conflito NÃO cria um segundo Job
    }

    [Fact]
    public async Task ConcurrentSameKeyProducesExactlyOneJob()
    {
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, Clock);
        var scope = SqlServerFixture.NewScope();
        var key = Guid.NewGuid();
        var environmentId = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => inbox.EnqueueIdempotentAsync(BuildCommand(scope, environmentId), key, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Select(r => r.JobId.Value).Distinct()); // todas recebem o MESMO JobId
        Assert.Equal(1, results.Count(r => r.Created)); // exatamente uma criação
        Assert.Equal(1, await CountJobsAsync(scope));
        Assert.Equal(1, await CountCommandsAsync(scope, key));
    }

    [Fact]
    public async Task EnvironmentCatalogIsScopedAntiIdor()
    {
        var catalog = new SqlEvEnvironmentCatalog(_fixture.Factory);
        var tenant = new Domain.IdentityAndAccess.TenantId(Guid.NewGuid());
        var scope1 = new TenantScope(tenant, new Domain.Projects.ProjectId(Guid.NewGuid()));
        var scope2 = new TenantScope(tenant, new Domain.Projects.ProjectId(Guid.NewGuid())); // mesmo tenant, outro projeto
        var otherTenant = SqlServerFixture.NewScope();
        var environmentId = new EvEnvironmentId(Guid.NewGuid());

        await SeedEnvironmentAsync(scope1, environmentId, "site-a", "dir-a.local");

        Assert.NotNull(await catalog.GetAsync(scope1, environmentId, CancellationToken.None));
        Assert.Null(await catalog.GetAsync(scope2, environmentId, CancellationToken.None));      // cross-project
        Assert.Null(await catalog.GetAsync(otherTenant, environmentId, CancellationToken.None)); // cross-tenant
    }

    private static EvDiscoveryCommand BuildCommand(TenantScope scope, Guid environmentId) =>
        new(
            scope,
            new EvEnvironmentId(environmentId),
            "site-a",
            "dir-a.contoso.local",
            "operator@contoso.local",
            CorrelationId.New(),
            new EvDiscoveryCommandContext(
                EvDiscoveryCommandContext.CurrentSchemaVersion, 1, new Sha256Hash(new string('a', 64)),
                EvDiscoveryPolicy.Default.PolicyVersion));

    private Task SeedEnvironmentAsync(TenantScope scope, EvEnvironmentId environmentId, string site, string dir) => ExecAsync(scope,
        """
        INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
        VALUES (@env, @tenant, @project, @site, @dir);
        """,
        command =>
        {
            command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = site });
            command.Parameters.Add(new SqlParameter("@dir", SqlDbType.NVarChar, 260) { Value = dir });
        });

    private async Task<int> CountJobsAsync(TenantScope scope) =>
        await ScalarAsync(scope, "SELECT COUNT(*) FROM dbo.jobs WHERE project_id = @project;", _ => { });

    private async Task<int> CountCommandsAsync(TenantScope scope, Guid key) =>
        await ScalarAsync(scope,
            "SELECT COUNT(*) FROM dbo.ev_discovery_commands WHERE project_id = @project AND idempotency_key = @key;",
            command => command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = key }));

    private async Task<int> ScalarAsync(TenantScope scope, string sql, Action<SqlCommand> bind)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        bind(command);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecAsync(TenantScope scope, string sql, Action<SqlCommand> bind)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        bind(command);
        await command.ExecuteNonQueryAsync();
    }
}
