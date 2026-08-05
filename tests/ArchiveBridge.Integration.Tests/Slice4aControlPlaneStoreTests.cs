using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A — identidade do portal e read-model do plano de controle contra SQL Server real. Prova: hash de
/// senha (round-trip, senha errada, hash adulterado); store de usuários (criação atômica, papéis, login
/// duplicado e papel desconhecido recusados fail-closed); auditoria de login (sucesso/falha, ordem); e o
/// read-model respeitando o ISOLAMENTO POR TENANT (RLS) — um tenant nunca enxerga projetos/jobs/ambientes de
/// outro. As leituras usam a MESMA fábrica de conexões da produção (SESSION_CONTEXT por tenant).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aControlPlaneStoreTests(SqlServerFixture fixture)
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private readonly SqlServerFixture _fixture = fixture;

    [Fact]
    public void PasswordHashRoundTripsAndRejectsWrongAndTampered()
    {
        var hash = Hasher.Hash("Corr3ct-Horse!");
        Assert.True(Hasher.Verify("Corr3ct-Horse!", hash));
        Assert.False(Hasher.Verify("wrong", hash));
        Assert.NotEqual("Corr3ct-Horse!", hash.HashBase64); // nunca guarda a senha em claro

        var tampered = hash with { HashBase64 = Convert.ToBase64String(new byte[32]) };
        Assert.False(Hasher.Verify("Corr3ct-Horse!", tampered));

        var unknownAlgorithm = hash with { Algorithm = "MD5" };
        Assert.False(Hasher.Verify("Corr3ct-Horse!", unknownAlgorithm)); // formato desconhecido = fail-closed
    }

    [Fact]
    public void TwoHashesOfSamePasswordDifferBySalt()
    {
        var a = Hasher.Hash("same-password");
        var b = Hasher.Hash("same-password");
        Assert.NotEqual(a.SaltBase64, b.SaltBase64);
        Assert.NotEqual(a.HashBase64, b.HashBase64);
        Assert.True(Hasher.Verify("same-password", a) && Hasher.Verify("same-password", b));
    }

    [Fact]
    public async Task UserStoreCreatesFindsAndLoadsRoles()
    {
        var store = new SqlPortalUserStore(_fixture.ConnectionString);
        var scope = SqlServerFixture.NewScope();
        var username = "user_" + Guid.NewGuid().ToString("N");

        await store.CreateAsync(
            new PortalUserRegistration(username, "Fulano", scope.Tenant, scope.Project, Hasher.Hash("s3cret!"),
                [PortalRoles.Operator, PortalRoles.Auditor]),
            CancellationToken.None);

        var found = await store.FindByUsernameAsync(username, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(username, found!.Username);
        Assert.Equal(scope.Tenant.Value, found.Tenant.Value);
        Assert.True(found.Enabled);
        Assert.True(Hasher.Verify("s3cret!", found.Password));
        Assert.Equal([PortalRoles.Auditor, PortalRoles.Operator], found.Roles.OrderBy(r => r, StringComparer.Ordinal));
        Assert.Null(await store.FindByUsernameAsync("nao_existe_" + Guid.NewGuid().ToString("N"), CancellationToken.None));
    }

    [Fact]
    public async Task UserStoreRejectsDuplicateUsernameAndUnknownRole()
    {
        var store = new SqlPortalUserStore(_fixture.ConnectionString);
        var scope = SqlServerFixture.NewScope();
        var username = "dup_" + Guid.NewGuid().ToString("N");
        var registration = new PortalUserRegistration(
            username, "Dup", scope.Tenant, scope.Project, Hasher.Hash("pw"), [PortalRoles.Operator]);

        await store.CreateAsync(registration, CancellationToken.None);
        await Assert.ThrowsAsync<SqlException>(() => store.CreateAsync(registration, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(
            new PortalUserRegistration("x_" + Guid.NewGuid().ToString("N"), "X", scope.Tenant, scope.Project,
                Hasher.Hash("pw"), ["Superuser"]),
            CancellationToken.None));
    }

    [Fact]
    public async Task SignInAuditRecordsSuccessAndFailureInOrder()
    {
        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        var marker = "audit_" + Guid.NewGuid().ToString("N");
        await audit.RecordAsync(new PortalSignInEvent(marker, false, "invalid-credentials", "10.0.0.1", DateTimeOffset.UtcNow), CancellationToken.None);
        await audit.RecordAsync(new PortalSignInEvent(marker, true, "ok", "10.0.0.1", DateTimeOffset.UtcNow.AddSeconds(1)), CancellationToken.None);

        var recent = await audit.RecentAsync(100, CancellationToken.None);
        var mine = recent.Where(e => e.Username == marker).ToList();
        Assert.Equal(2, mine.Count);
        Assert.True(mine[0].Succeeded); // mais recente primeiro
        Assert.False(mine[1].Succeeded);
    }

    [Fact]
    public async Task ReadModelIsolatesProjectsAndJobsByTenant()
    {
        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var tenantA = SqlServerFixture.NewScope();
        var tenantB = SqlServerFixture.NewScope();

        await SeedBaseProjectAsync(tenantA);
        await SeedProjectAsync(tenantA, "Projeto A");
        await SeedJobAsync(tenantA, workload: 0, state: 4); // Failed
        await SeedBaseProjectAsync(tenantB);
        await SeedProjectAsync(tenantB, "Projeto B");

        var projectsA = await queries.ListProjectsAsync(tenantA, CancellationToken.None);
        Assert.Contains(projectsA, p => p.Name == "Projeto A");
        Assert.DoesNotContain(projectsA, p => p.Name == "Projeto B"); // RLS: nunca vaza outro tenant

        var dashA = await queries.GetDashboardAsync(tenantA, CancellationToken.None);
        Assert.True(dashA.Projects >= 1);
        Assert.True(dashA.JobsFailed >= 1);

        var dashB = await queries.GetDashboardAsync(tenantB, CancellationToken.None);
        Assert.Equal(0, dashB.JobsFailed); // jobs de A não contam para B
    }

    [Fact]
    public async Task ReadModelMapsEvDiscoveryReadyResult()
    {
        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var scope = SqlServerFixture.NewScope();
        var environmentId = Guid.NewGuid();
        await SeedBaseProjectAsync(scope);
        await SeedEvEnvironmentAsync(scope, environmentId, "Site-1", "dc1");
        await SeedEvDiscoveryRunAsync(scope, environmentId, version: 1, status: 2, resultStatus: 2, resultCode: "EV_READY"); // Ready

        var environments = await queries.ListEvEnvironmentsAsync(scope, CancellationToken.None);
        var env = Assert.Single(environments, e => e.EnvironmentId == environmentId);
        Assert.Equal("Site-1", env.SiteName);
        Assert.NotNull(env.LatestDiscovery);
        Assert.Equal("Ready", env.LatestDiscovery!.ResultStatus);
        Assert.Equal(1, env.LatestDiscovery.DiscoveryVersion);

        var dash = await queries.GetDashboardAsync(scope, CancellationToken.None);
        Assert.True(dash.EvEnvironments >= 1);
        Assert.True(dash.EvReady >= 1);
    }

    // ---- Semeadura via conexão de tenant (respeita a RLS block predicate e usa os grants da aplicação) ----

    // Projeto-base do Slice 1 (dbo.projects): as tabelas de agregado o referenciam por FK composta.
    private Task SeedBaseProjectAsync(TenantScope scope) => ExecAsync(scope,
        "INSERT INTO dbo.projects (project_id, tenant_id) VALUES (@project, @tenant);",
        command => command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value }));

    private Task SeedProjectAsync(TenantScope scope, string name) => ExecAsync(scope,
        """
        INSERT INTO dbo.migration_projects
            (project_id, tenant_id, project_name, project_owner, target_tenant, target_archive_policy,
             configuration_version, configuration_hash, status)
        VALUES (@project, @tenant, @name, 'owner', 'target.example', 0, 1, @hash, 0);
        """,
        command =>
        {
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = name });
            command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = new string('a', 64) });
        });

    private Task SeedJobAsync(TenantScope scope, byte workload, byte state) => ExecAsync(scope,
        """
        INSERT INTO dbo.jobs (job_id, tenant_id, project_id, workload, state)
        VALUES (@id, @tenant, @project, @workload, @state);
        """,
        command =>
        {
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = workload });
            command.Parameters.Add(new SqlParameter("@state", SqlDbType.TinyInt) { Value = state });
        });

    private Task SeedEvEnvironmentAsync(TenantScope scope, Guid environmentId, string site, string dir) => ExecAsync(scope,
        """
        INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
        VALUES (@id, @tenant, @project, @site, @dir);
        """,
        command =>
        {
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = environmentId });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = site });
            command.Parameters.Add(new SqlParameter("@dir", SqlDbType.NVarChar, 260) { Value = dir });
        });

    private Task SeedEvDiscoveryRunAsync(
        TenantScope scope, Guid environmentId, int version, byte status, byte resultStatus, string resultCode) => ExecAsync(scope,
        """
        INSERT INTO dbo.ev_discovery_runs
            (environment_id, discovery_version, tenant_id, project_id, run_id, status, result_status, result_code,
             observed_version, discovery_schema_version, configuration_hash, evidence_hash, evidence_path,
             evidence_size_bytes, correlation_id, started_at_utc, completed_at_utc)
        VALUES (@env, @version, @tenant, @project, @run, @status, @resultStatus, @resultCode,
             '15.1', 1, @configHash, @evidenceHash, '/evidence/ev/1', 1024, @correlation, SYSUTCDATETIME(), SYSUTCDATETIME());
        """,
        command =>
        {
            command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId });
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
            command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = status });
            command.Parameters.Add(new SqlParameter("@resultStatus", SqlDbType.TinyInt) { Value = resultStatus });
            command.Parameters.Add(new SqlParameter("@resultCode", SqlDbType.NVarChar, 64) { Value = resultCode });
            command.Parameters.Add(new SqlParameter("@configHash", SqlDbType.Char, 64) { Value = new string('b', 64) });
            command.Parameters.Add(new SqlParameter("@evidenceHash", SqlDbType.Char, 64) { Value = new string('c', 64) });
            command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        });

    private async Task ExecAsync(TenantScope scope, string sql, Action<SqlCommand> bind)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        bind(command);
        await command.ExecuteNonQueryAsync();
    }
}
