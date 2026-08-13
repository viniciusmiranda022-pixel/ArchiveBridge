using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Passo 5 — corretude da paginação keyset/seek contra SQL Server real, nas três trilhas (histórico de
/// evidências, auditoria operacional e autenticação). Prova travessia completa sem lacunas/duplicatas, o
/// desempate determinístico com timestamps iguais, estabilidade sob inserção entre páginas, isolamento
/// cross-project/cross-tenant, inércia de curingas/injeção (dados, nunca sintaxe), teto de tamanho de página
/// e visibilidade histórica de <c>Superseded</c>.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aPagedReadTests(SqlServerFixture fixture)
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private static readonly DateTimeOffset Base = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly SqlServerFixture _fixture = fixture;

    // ===================== AUDITORIA OPERACIONAL =====================

    [Fact]
    public async Task OperationalHistoryTraversesAllWithoutGapsOrDuplicates()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var userId = await SeedUserAsync(scope);
        await SeedOperationalAsync(scope, userId, 130, index => Base.AddSeconds(index));

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        var filter = new PortalOperationalAuditFilter(null, null, null, null, null, null);
        var all = await TraverseAsync<PortalOperationalAuditEvent, AuditSeekPosition>(
            (size, after) => audit.SearchAsync(scope, filter, size, after, CancellationToken.None), 25);

        var ids = all.Select(e => e.ResourceId).ToList();
        Assert.Equal(130, ids.Count);
        Assert.Equal(130, ids.Distinct().Count()); // sem duplicatas
        Assert.True(Enumerable.Range(0, 130).Select(Resource).ToHashSet().SetEquals(ids)); // sem lacunas
        Assert.True(all.Select(e => e.OccurredAtUtc).SequenceEqual(all.Select(e => e.OccurredAtUtc).OrderByDescending(x => x)));
    }

    [Fact]
    public async Task OperationalHistoryIsDeterministicWhenTimestampsAreEqual()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var userId = await SeedUserAsync(scope);
        await SeedOperationalAsync(scope, userId, 120, _ => Base); // TODOS com o MESMO occurred_at

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        var filter = new PortalOperationalAuditFilter(null, null, null, null, null, null);
        var ids = (await TraverseAsync<PortalOperationalAuditEvent, AuditSeekPosition>(
            (size, after) => audit.SearchAsync(scope, filter, size, after, CancellationToken.None), 25))
            .Select(e => e.ResourceId).ToList();

        Assert.Equal(120, ids.Count);
        Assert.Equal(120, ids.Distinct().Count()); // o desempate por event_id impede duplicata/loop/perda
    }

    [Fact]
    public async Task OperationalInsertBetweenPagesDoesNotDuplicateOrShiftEarlierRows()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var userId = await SeedUserAsync(scope);
        await SeedOperationalAsync(scope, userId, 60, index => Base.AddSeconds(index));

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        var filter = new PortalOperationalAuditFilter(null, null, null, null, null, null);

        var page1 = await audit.SearchAsync(scope, filter, 25, null, CancellationToken.None);
        Assert.True(page1.HasMore);
        var page1Ids = page1.Items.Select(e => e.ResourceId).ToHashSet();

        // Insere um registro MAIS NOVO que a fronteira da página 1.
        await SeedOperationalAsync(scope, userId, 1, _ => Base.AddSeconds(1000), usernamePrefix: "inserted");

        var page2 = await audit.SearchAsync(scope, filter, 25, page1.NextPosition, CancellationToken.None);
        Assert.All(page2.Items, e => Assert.DoesNotContain(e.ResourceId, page1Ids)); // nenhuma linha da p1 reaparece
    }

    [Fact]
    public async Task OperationalIsIsolatedAcrossProjectsAndTenants()
    {
        var a1 = SqlServerFixture.NewScope();
        var a2 = new TenantScope(a1.Tenant, new ProjectId(Guid.NewGuid()));
        var b3 = SqlServerFixture.NewScope();
        foreach (var s in new[] { a1, a2, b3 })
        {
            await SeedProjectAsync(s);
        }

        var user1 = await SeedUserAsync(a1);
        var user2 = await SeedUserAsync(a2);
        var user3 = await SeedUserAsync(b3);
        var foreignCorrelation = Guid.NewGuid();
        await SeedOperationalAsync(a1, user1, 5, index => Base.AddSeconds(index));
        await SeedOneOperationalAsync(a2, user2, foreignCorrelation, "other-project");
        await SeedOneOperationalAsync(b3, user3, Guid.NewGuid(), "other-tenant");

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        // Mesmo filtrando por uma correlação existente em OUTRO projeto, A/P1 não enxerga nada além do seu.
        var filter = new PortalOperationalAuditFilter(null, null, null, foreignCorrelation, null, null);
        var page = await audit.SearchAsync(a1, filter, 50, null, CancellationToken.None);
        Assert.Empty(page.Items);

        var all = await audit.SearchAsync(a1, new PortalOperationalAuditFilter(null, null, null, null, null, null), 50, null, CancellationToken.None);
        Assert.Equal(5, all.Items.Count);
        Assert.All(all.Items, e => Assert.NotEqual("other-project", e.Username));
        Assert.All(all.Items, e => Assert.NotEqual("other-tenant", e.Username));
    }

    [Fact]
    public async Task OperationalUsernamePrefixTreatsWildcardsAsLiteralData()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var userId = await SeedUserAsync(scope);
        await SeedOneOperationalAsync(scope, userId, Guid.NewGuid(), "alice");
        await SeedOneOperationalAsync(scope, userId, Guid.NewGuid(), "alice_bob");   // sublinhado LITERAL
        await SeedOneOperationalAsync(scope, userId, Guid.NewGuid(), "aliceXbob");   // 'X' onde estaria o curinga

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        // Sem escaping, "alice_" casaria "aliceXbob" (o _ é curinga de 1 char). Com escaping, casa só "alice_bob".
        var byUnderscore = await audit.SearchAsync(
            scope, new PortalOperationalAuditFilter(null, "alice_", null, null, null, null), 50, null, CancellationToken.None);
        Assert.Single(byUnderscore.Items);
        Assert.Equal("alice_bob", byUnderscore.Items[0].Username);

        // Entradas hostis são apenas DADOS: nenhuma linha casa, nenhuma alteração de SQL, tabela íntegra.
        foreach (var hostile in new[] { "' OR 1=1 --", "%'; DROP TABLE dbo.projects; --", "%", "[a-z]", "\\" })
        {
            var page = await audit.SearchAsync(
                scope, new PortalOperationalAuditFilter(null, hostile, null, null, null, null), 50, null, CancellationToken.None);
            Assert.Empty(page.Items);
        }

        // A tabela de projetos continua existindo/íntegra (a "injeção" não executou nada).
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var check = new SqlCommand("SELECT COUNT(*) FROM dbo.projects WHERE project_id = @p;", tenant.Connection);
        check.Parameters.Add(new SqlParameter("@p", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        Assert.Equal(1, Convert.ToInt32(await check.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OperationalPageSizeIsBounded()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var userId = await SeedUserAsync(scope);
        await SeedOperationalAsync(scope, userId, 150, index => Base.AddSeconds(index));

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        var page = await audit.SearchAsync(
            scope, new PortalOperationalAuditFilter(null, null, null, null, null, null), 1_000_000, null, CancellationToken.None);
        Assert.Equal(KeysetPaging.MaxPageSize, page.Items.Count); // teto = 100, nunca 150
        Assert.True(page.HasMore);
    }

    // ===================== AUTENTICAÇÃO =====================

    [Fact]
    public async Task SignInHistoryTraversesTenantWideWithoutGapsOrDuplicates()
    {
        var tenant = new TenantId(Guid.NewGuid());
        for (var i = 0; i < 110; i++)
        {
            await SeedSignInAsync(tenant.Value, $"r{i:D5}", Base.AddSeconds(i));
        }

        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        var filter = new PortalSignInAuditFilter(null, null, null, null, null);
        var all = await TraverseAsync<PortalSignInEvent, AuditSeekPosition>(
            (size, after) => audit.SearchAsync(tenant, filter, size, after, CancellationToken.None), 25);

        var ids = all.Select(e => e.Reason).ToList();
        Assert.Equal(110, ids.Count);
        Assert.Equal(110, ids.Distinct().Count());
    }

    [Fact]
    public async Task SignInIsIsolatedByTenantAndHidesTenantNull()
    {
        var tenantA = new TenantId(Guid.NewGuid());
        var tenantB = new TenantId(Guid.NewGuid());
        await SeedSignInAsync(tenantA.Value, "a-only", Base);
        await SeedSignInAsync(tenantB.Value, "b-only", Base);
        await SeedSignInAsync(null, "null-tenant", Base); // usuário inexistente ⇒ tenant nulo

        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        var page = await audit.SearchAsync(tenantA, new PortalSignInAuditFilter(null, null, null, null, null), 50, null, CancellationToken.None);
        var reasons = page.Items.Select(e => e.Reason).ToHashSet();
        Assert.Contains("a-only", reasons);
        Assert.DoesNotContain("b-only", reasons);      // outro tenant NUNCA aparece
        Assert.DoesNotContain("null-tenant", reasons); // eventos com tenant nulo não são exibidos
    }

    // ===================== HISTÓRICO DE EVIDÊNCIAS =====================

    [Fact]
    public async Task EvidenceHistoryTraversesAllIncludingSupersededAndExcludesImmature()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);

        // UX_evd_single_current permite só UMA execução MADURA terminal (2..5) por ambiente. Para um histórico
        // com muitas linhas, cada execução usa um ambiente próprio. 40 Ready + 1 Superseded = 41 maduras
        // (Superseded é histórico VISÍVEL); 1 Pending + 1 Discovering são IMATURAS (ocultas).
        var matureEnvironments = new HashSet<Guid>();
        for (var i = 0; i < 40; i++)
        {
            matureEnvironments.Add(await SeedRunOnNewEnvironmentAsync(scope, status: 2, resultStatus: 2, Base.AddSeconds(i)));
        }

        matureEnvironments.Add(await SeedRunOnNewEnvironmentAsync(scope, status: 6, resultStatus: 2, Base.AddSeconds(100)));
        var pendingEnvironment = await SeedRunOnNewEnvironmentAsync(scope, status: 0, resultStatus: 2, Base.AddSeconds(200));
        var discoveringEnvironment = await SeedRunOnNewEnvironmentAsync(scope, status: 1, resultStatus: 2, Base.AddSeconds(201));

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var filter = new EvEvidenceFilter(null, null, null, null, null);
        var all = await TraverseAsync<EvEvidenceListItem, EvidenceSeekPosition>(
            (size, after) => queries.SearchEvEvidencePageAsync(scope, filter, size, after, CancellationToken.None), 15);

        Assert.Equal(41, all.Count);
        Assert.Equal(41, all.Select(e => e.EnvironmentId).Distinct().Count()); // sem lacunas/duplicatas
        Assert.True(matureEnvironments.SetEquals(all.Select(e => e.EnvironmentId)));
        Assert.Contains(all, e => e.LifecycleStatus == nameof(EvDiscoveryStatus.Superseded)); // Superseded visível
        Assert.DoesNotContain(all, e => e.EnvironmentId == pendingEnvironment || e.EnvironmentId == discoveringEnvironment);
    }

    [Fact]
    public async Task EvidenceHistoryIsIsolatedAcrossProjectsAndTenants()
    {
        var a1 = SqlServerFixture.NewScope();
        var a2 = new TenantScope(a1.Tenant, new ProjectId(Guid.NewGuid()));
        var b3 = SqlServerFixture.NewScope();
        foreach (var s in new[] { a1, a2, b3 })
        {
            await SeedProjectAsync(s);
        }

        var envA1 = Guid.NewGuid();
        var envA2 = Guid.NewGuid();
        var envB3 = Guid.NewGuid();
        await SeedEnvironmentAsync(a1, envA1, "a1");
        await SeedEnvironmentAsync(a2, envA2, "a2");
        await SeedEnvironmentAsync(b3, envB3, "b3");
        await SeedRunAsync(a1, envA1, 1, 2, 2, Base);
        await SeedRunAsync(a2, envA2, 1, 2, 2, Base);
        await SeedRunAsync(b3, envB3, 1, 2, 2, Base);

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        // Mesmo filtrando pelo environment de OUTRO projeto/tenant, A/P1 não enxerga nada além do seu.
        Assert.Empty((await queries.SearchEvEvidencePageAsync(a1, new EvEvidenceFilter(null, envA2, null, null, null), 50, null, CancellationToken.None)).Items);
        Assert.Empty((await queries.SearchEvEvidencePageAsync(a1, new EvEvidenceFilter(null, envB3, null, null, null), 50, null, CancellationToken.None)).Items);

        var own = await queries.SearchEvEvidencePageAsync(a1, new EvEvidenceFilter(null, null, null, null, null), 50, null, CancellationToken.None);
        Assert.Single(own.Items);
        Assert.Equal(envA1, own.Items[0].EnvironmentId);
    }

    [Fact]
    public async Task EvidenceSitePrefixTreatsWildcardsAsLiteralData()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var envLiteral = Guid.NewGuid();
        var envOther = Guid.NewGuid();
        await SeedEnvironmentAsync(scope, envLiteral, "prod_site");
        await SeedEnvironmentAsync(scope, envOther, "prodXsite");
        await SeedRunAsync(scope, envLiteral, 1, 2, 2, Base);
        await SeedRunAsync(scope, envOther, 1, 2, 2, Base.AddSeconds(1));

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var page = await queries.SearchEvEvidencePageAsync(
            scope, new EvEvidenceFilter("prod_", null, null, null, null), 50, null, CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal("prod_site", page.Items[0].SiteName); // '_' literal, não curinga
    }

    [Fact]
    public async Task EvidenceHistoryIsDeterministicWhenTimestampsAreEqual()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);

        // 120 execuções MADURAS (uma por ambiente, pela UX_evd_single_current) com EXATAMENTE o mesmo
        // completed_at_utc: o desempate (environment_id DESC, discovery_version DESC) tem de resolver tudo.
        var expected = new HashSet<Guid>();
        for (var i = 0; i < 120; i++)
        {
            expected.Add(await SeedRunOnNewEnvironmentAsync(scope, status: 2, resultStatus: 2, Base));
        }

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var filter = new EvEvidenceFilter(null, null, null, null, null);
        var all = await TraverseAsync<EvEvidenceListItem, EvidenceSeekPosition>(
            (size, after) => queries.SearchEvEvidencePageAsync(scope, filter, size, after, CancellationToken.None), 25);

        Assert.Equal(120, all.Count);                                   // travessia termina
        Assert.Equal(120, all.Select(e => e.EnvironmentId).Distinct().Count()); // zero duplicatas/lacunas
        Assert.True(expected.SetEquals(all.Select(e => e.EnvironmentId)));
    }

    [Fact]
    public async Task EvidenceInsertBetweenPagesDoesNotDuplicateEarlierRows()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        for (var i = 0; i < 120; i++)
        {
            await SeedRunOnNewEnvironmentAsync(scope, status: 2, resultStatus: 2, Base.AddSeconds(i));
        }

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var filter = new EvEvidenceFilter(null, null, null, null, null);

        var page1 = await queries.SearchEvEvidencePageAsync(scope, filter, 25, null, CancellationToken.None);
        Assert.True(page1.HasMore);
        var page1Ids = page1.Items.Select(e => e.EnvironmentId).ToHashSet();

        // Insere uma evidência MAIS NOVA que a fronteira da página 1 (num novo ambiente).
        await SeedRunOnNewEnvironmentAsync(scope, status: 2, resultStatus: 2, Base.AddSeconds(10_000));

        var page2 = await queries.SearchEvEvidencePageAsync(scope, filter, 25, page1.NextPosition, CancellationToken.None);
        Assert.All(page2.Items, e => Assert.DoesNotContain(e.EnvironmentId, page1Ids)); // nenhuma linha da p1 reaparece
    }

    [Fact]
    public async Task EvidencePageSizeIsBounded()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        for (var i = 0; i < 120; i++)
        {
            await SeedRunOnNewEnvironmentAsync(scope, status: 2, resultStatus: 2, Base.AddSeconds(i));
        }

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var page = await queries.SearchEvEvidencePageAsync(
            scope, new EvEvidenceFilter(null, null, null, null, null), 1_000_000, null, CancellationToken.None);
        Assert.Equal(KeysetPaging.MaxPageSize, page.Items.Count); // teto = 100, nunca 120
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task EvidenceFiltersMatchEnvironmentResultStatusAndDateRange()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var envReadyEarly = Guid.NewGuid();
        var envFailedMid = Guid.NewGuid();
        var envReadyLate = Guid.NewGuid();
        await SeedEnvironmentAsync(scope, envReadyEarly, "ready-early");
        await SeedEnvironmentAsync(scope, envFailedMid, "failed-mid");
        await SeedEnvironmentAsync(scope, envReadyLate, "ready-late");
        await SeedRunAsync(scope, envReadyEarly, 1, status: 2, resultStatus: 2, Base);                 // Ready @ 0
        await SeedRunAsync(scope, envFailedMid, 1, status: 5, resultStatus: 5, Base.AddSeconds(100));  // Failed @ 100
        await SeedRunAsync(scope, envReadyLate, 1, status: 2, resultStatus: 2, Base.AddSeconds(200));  // Ready @ 200

        var queries = new SqlControlPlaneQueries(_fixture.Factory);

        // EnvironmentId
        var byEnv = await queries.SearchEvEvidencePageAsync(
            scope, new EvEvidenceFilter(null, envFailedMid, null, null, null), 50, null, CancellationToken.None);
        Assert.Equal(envFailedMid, Assert.Single(byEnv.Items).EnvironmentId);

        // ResultStatus (conjunto fechado)
        var byFailed = await queries.SearchEvEvidencePageAsync(
            scope, new EvEvidenceFilter(null, null, EvDiscoveryStatus.Failed, null, null), 50, null, CancellationToken.None);
        Assert.Equal(envFailedMid, Assert.Single(byFailed.Items).EnvironmentId);
        var byReady = await queries.SearchEvEvidencePageAsync(
            scope, new EvEvidenceFilter(null, null, EvDiscoveryStatus.Ready, null, null), 50, null, CancellationToken.None);
        Assert.Equal(new HashSet<Guid> { envReadyEarly, envReadyLate }, byReady.Items.Select(e => e.EnvironmentId).ToHashSet());

        // Intervalo UTC [From, To] — só a execução do meio.
        var byRange = await queries.SearchEvEvidencePageAsync(
            scope, new EvEvidenceFilter(null, null, null, Base.AddSeconds(50), Base.AddSeconds(150)), 50, null, CancellationToken.None);
        Assert.Equal(envFailedMid, Assert.Single(byRange.Items).EnvironmentId);
    }

    [Fact]
    public async Task ForgedCursorFromAnotherScopeCannotCrossScope()
    {
        // Prova de que CURSOR != AUTORIZAÇÃO: um cursor BEM-FORMADO cujas chaves de seek vêm de A/P2 ou B, usado
        // por um principal de A/P1, só desloca o ponto temporal DENTRO de A/P1 — nunca revela A/P2 nem B.
        var a1 = SqlServerFixture.NewScope();
        var a2 = new TenantScope(a1.Tenant, new ProjectId(Guid.NewGuid()));
        var b3 = SqlServerFixture.NewScope();
        foreach (var s in new[] { a1, a2, b3 })
        {
            await SeedProjectAsync(s);
        }

        var env1a = await SeedRunOnNewEnvironmentAsync(a1, status: 2, resultStatus: 2, Base);
        var env2a = await SeedRunOnNewEnvironmentAsync(a1, status: 2, resultStatus: 2, Base.AddSeconds(10));
        var foreignA2 = await SeedRunOnNewEnvironmentAsync(a2, status: 2, resultStatus: 2, Base.AddSeconds(15));
        var foreignB3 = await SeedRunOnNewEnvironmentAsync(b3, status: 2, resultStatus: 2, Base.AddSeconds(15));

        // Constrói MANUALMENTE um cursor bem-formado a partir de dados de A/P2 e o valida com o fingerprint dos
        // filtros vazios do PageModel de A/P1 (por isso é aceito pelo handler — mesmo assim não troca o escopo).
        var forgedPosition = new EvidenceSeekPosition(Base.AddSeconds(15), foreignA2, 1);
        var fingerprint = FilterFingerprint.Compute(null, null, null, null, null);
        var cursor = KeysetCursor.Encode(fingerprint, forgedPosition);
        Assert.True(KeysetCursor.TryDecode<EvidenceSeekPosition>(cursor, fingerprint, out var decoded));

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var page = await queries.SearchEvEvidencePageAsync(
            a1, new EvEvidenceFilter(null, null, null, null, null), 50, decoded, CancellationToken.None);

        var seen = page.Items.Select(e => e.EnvironmentId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { env1a, env2a }, seen);  // só A/P1, mais antigas que a posição forjada
        Assert.DoesNotContain(foreignA2, seen);                  // A/P2 jamais aparece
        Assert.DoesNotContain(foreignB3, seen);                  // B jamais aparece
    }

    [Fact]
    public async Task SignInHistoryIsDeterministicWhenTimestampsAreEqual()
    {
        var tenant = new TenantId(Guid.NewGuid());
        for (var i = 0; i < 120; i++)
        {
            await SeedSignInAsync(tenant.Value, $"r{i:D5}", Base); // TODOS com o MESMO occurred_at
        }

        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        var filter = new PortalSignInAuditFilter(null, null, null, null, null);
        var reasons = (await TraverseAsync<PortalSignInEvent, AuditSeekPosition>(
            (size, after) => audit.SearchAsync(tenant, filter, size, after, CancellationToken.None), 25))
            .Select(e => e.Reason).ToList();

        Assert.Equal(120, reasons.Count);
        Assert.Equal(120, reasons.Distinct().Count()); // event_id resolve o empate: sem duplicata/lacuna/loop
    }

    [Fact]
    public async Task SignInPageSizeIsBounded()
    {
        var tenant = new TenantId(Guid.NewGuid());
        for (var i = 0; i < 120; i++)
        {
            await SeedSignInAsync(tenant.Value, $"r{i:D5}", Base.AddSeconds(i));
        }

        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        var page = await audit.SearchAsync(
            tenant, new PortalSignInAuditFilter(null, null, null, null, null), 1_000_000, null, CancellationToken.None);
        Assert.Equal(KeysetPaging.MaxPageSize, page.Items.Count); // teto = 100
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task SignInFiltersMatchUsernameSucceededReasonAndDateRange()
    {
        var tenant = new TenantId(Guid.NewGuid());
        await SeedSignInFullAsync(tenant.Value, "alice", succeeded: true, "login-ok", Base);
        await SeedSignInFullAsync(tenant.Value, "bob", succeeded: false, "bad-password", Base.AddSeconds(100));

        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);

        var byUser = await audit.SearchAsync(tenant, new PortalSignInAuditFilter("ali", null, null, null, null), 50, null, CancellationToken.None);
        Assert.Equal("alice", Assert.Single(byUser.Items).Username);

        var byFail = await audit.SearchAsync(tenant, new PortalSignInAuditFilter(null, false, null, null, null), 50, null, CancellationToken.None);
        Assert.Equal("bob", Assert.Single(byFail.Items).Username);

        var byReason = await audit.SearchAsync(tenant, new PortalSignInAuditFilter(null, null, "bad-password", null, null), 50, null, CancellationToken.None);
        Assert.Equal("bob", Assert.Single(byReason.Items).Username);

        var byRange = await audit.SearchAsync(
            tenant, new PortalSignInAuditFilter(null, null, null, Base.AddSeconds(50), Base.AddSeconds(150)), 50, null, CancellationToken.None);
        Assert.Equal("bob", Assert.Single(byRange.Items).Username);
    }

    [Fact]
    public async Task OperationalFiltersMatchActionCodeSucceededAndDateRange()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var userId = await SeedUserAsync(scope);
        await SeedOneOperationalFullAsync(scope, userId, "act.approve", succeeded: true, "u1", Base);
        await SeedOneOperationalFullAsync(scope, userId, "act.reject", succeeded: false, "u2", Base.AddSeconds(100));
        await SeedOneOperationalFullAsync(scope, userId, "act.approve", succeeded: true, "u3", Base.AddSeconds(200));

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);

        var byAction = await audit.SearchAsync(scope, new PortalOperationalAuditFilter("act.reject", null, null, null, null, null), 50, null, CancellationToken.None);
        Assert.Equal("u2", Assert.Single(byAction.Items).Username);

        var byFail = await audit.SearchAsync(scope, new PortalOperationalAuditFilter(null, null, false, null, null, null), 50, null, CancellationToken.None);
        Assert.Equal("u2", Assert.Single(byFail.Items).Username);

        var byRange = await audit.SearchAsync(
            scope, new PortalOperationalAuditFilter(null, null, null, null, Base.AddSeconds(50), Base.AddSeconds(150)), 50, null, CancellationToken.None);
        Assert.Equal("u2", Assert.Single(byRange.Items).Username);

        var byApprove = await audit.SearchAsync(scope, new PortalOperationalAuditFilter("act.approve", null, null, null, null, null), 50, null, CancellationToken.None);
        var approveNames = byApprove.Items.Select(e => e.Username).ToHashSet();
        Assert.Equal(2, approveNames.Count);
        Assert.Contains("u1", approveNames);
        Assert.Contains("u3", approveNames);
    }

    // ===================== boundary do escaping LIKE (comprimento máximo) =====================

    [Fact]
    public async Task EvidenceSitePrefixEscapingHoldsAtMaxLength()
    {
        // Prefixo de site no comprimento MÁXIMO (100) saturado de metacaracteres ⇒ padrão escapado de 201 chars.
        // Com o parâmetro @sitePrefix dimensionado (NVARCHAR 201), o padrão não trunca: o '%' final é preservado
        // e o site literal (com sufixo) é encontrado; a semântica de curinga permanece INERTE (dados, não sintaxe).
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var prefix = MetaChars(100);
        var envLiteral = Guid.NewGuid();
        var envWildcard = Guid.NewGuid();
        await SeedEnvironmentAsync(scope, envLiteral, prefix + "-tail");        // começa com o prefixo LITERAL
        await SeedEnvironmentAsync(scope, envWildcard, new string('Z', 100) + "-tail"); // só casa se '%'/'_' fossem curinga
        await SeedRunAsync(scope, envLiteral, 1, status: 2, resultStatus: 2, Base);
        await SeedRunAsync(scope, envWildcard, 1, status: 2, resultStatus: 2, Base.AddSeconds(1));

        var queries = new SqlControlPlaneQueries(_fixture.Factory);
        var page = await queries.SearchEvEvidencePageAsync(
            scope, new EvEvidenceFilter(prefix, null, null, null, null), 50, null, CancellationToken.None);

        Assert.Equal(envLiteral, Assert.Single(page.Items).EnvironmentId); // achado, sem truncar
        Assert.DoesNotContain(envWildcard, page.Items.Select(e => e.EnvironmentId)); // curinga inerte
    }

    [Fact]
    public async Task OperationalUsernamePrefixEscapingHoldsAtMaxLength()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var userId = await SeedUserAsync(scope);
        var prefix = MetaChars(200); // padrão escapado = 401 chars (⇒ @usernamePrefix NVARCHAR 401)
        await SeedOneOperationalFullAsync(scope, userId, "act", true, prefix, Base);            // username = 200 metachars
        await SeedOneOperationalFullAsync(scope, userId, "act", true, new string('Z', 200), Base.AddSeconds(1));

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        var page = await audit.SearchAsync(
            scope, new PortalOperationalAuditFilter(null, prefix, null, null, null, null), 50, null, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(prefix, page.Items[0].Username); // casa o literal de 200 chars: padrão não truncou
    }

    [Fact]
    public async Task SignInUsernamePrefixEscapingHoldsAtMaxLength()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var prefix = MetaChars(200);
        await SeedSignInFullAsync(tenant.Value, prefix, succeeded: true, "r", Base);
        await SeedSignInFullAsync(tenant.Value, new string('Z', 200), succeeded: true, "r", Base.AddSeconds(1));

        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        var page = await audit.SearchAsync(
            tenant, new PortalSignInAuditFilter(prefix, null, null, null, null), 50, null, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(prefix, page.Items[0].Username);
    }

    // ===================== helpers =====================

    private static string Resource(int index) => $"r{index:D5}";

    // Sequência determinística SÓ de metacaracteres de LIKE (\ % _ [): cada char dobra ao escapar, então um
    // prefixo de N metacaracteres produz padrão escapado de 2N+1 chars (o pior caso de dimensionamento).
    private static string MetaChars(int length)
    {
        const string cycle = "%_[\\";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = cycle[i % cycle.Length];
        }

        return new string(chars);
    }

    private static async Task<List<T>> TraverseAsync<T, TPos>(
        Func<int, TPos?, Task<KeysetPage<T, TPos>>> fetch, int pageSize)
        where TPos : class, IKeysetPosition
    {
        var all = new List<T>();
        TPos? after = null;
        for (var guard = 0; guard < 100_000; guard++)
        {
            var page = await fetch(pageSize, after);
            Assert.True(page.Items.Count <= pageSize);
            all.AddRange(page.Items);
            if (!page.HasMore)
            {
                return all;
            }

            Assert.NotNull(page.NextPosition);
            after = page.NextPosition;
        }

        throw new Xunit.Sdk.XunitException("cursor traversal did not terminate");
    }

    private async Task SeedProjectAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "INSERT INTO dbo.projects (project_id, tenant_id) VALUES (@project, @tenant);", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedUserAsync(TenantScope scope)
    {
        var username = "pu_" + Guid.NewGuid().ToString("N");
        var store = new SqlPortalUserStore(_fixture.ConnectionString);
        await store.CreateAsync(
            new PortalUserRegistration(username, "U", scope.Tenant, scope.Project, Hasher.Hash("Str0ng!pw"), [PortalRoles.Auditor]),
            CancellationToken.None);
        var record = await store.FindByUsernameAsync(username, CancellationToken.None);
        return record!.UserId;
    }

    private async Task SeedOperationalAsync(
        TenantScope scope, Guid userId, int count, Func<int, DateTimeOffset> occurredAt, string usernamePrefix = "user")
    {
        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        for (var i = 0; i < count; i++)
        {
            await audit.RecordAsync(scope, new PortalOperationalAuditEvent(
                userId, usernamePrefix, "act", "res", Resource(i), i % 2 == 0, "reason", Guid.NewGuid(), occurredAt(i)),
                CancellationToken.None);
        }
    }

    private async Task SeedOneOperationalAsync(TenantScope scope, Guid userId, Guid correlation, string username)
    {
        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        await audit.RecordAsync(scope, new PortalOperationalAuditEvent(
            userId, username, "act", "res", "res-" + username, true, "reason", correlation, Base),
            CancellationToken.None);
    }

    // tenantIdToStore = o valor gravado em tenant_id (um GUID ou NULL, para o caso "usuário inexistente").
    private async Task SeedSignInAsync(Guid? tenantIdToStore, string reason, DateTimeOffset occurredAt)
    {
        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        await audit.RecordAsync(new PortalSignInEvent(
            tenantIdToStore, null, null, "user", true, reason, "127.0.0.1", Guid.NewGuid(), occurredAt),
            CancellationToken.None);
    }

    private async Task SeedSignInFullAsync(
        Guid? tenantIdToStore, string username, bool succeeded, string reason, DateTimeOffset occurredAt)
    {
        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        await audit.RecordAsync(new PortalSignInEvent(
            tenantIdToStore, null, null, username, succeeded, reason, "127.0.0.1", Guid.NewGuid(), occurredAt),
            CancellationToken.None);
    }

    private async Task SeedOneOperationalFullAsync(
        TenantScope scope, Guid userId, string action, bool succeeded, string username, DateTimeOffset occurredAt)
    {
        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        await audit.RecordAsync(scope, new PortalOperationalAuditEvent(
            userId, username, action, "res", "res-" + Guid.NewGuid().ToString("N"), succeeded, "reason", Guid.NewGuid(), occurredAt),
            CancellationToken.None);
    }

    private async Task SeedEnvironmentAsync(TenantScope scope, Guid environmentId, string site)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
            VALUES (@env, @tenant, @project, @site, 'directory-test');
            """, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = site });
        await command.ExecuteNonQueryAsync();
    }

    // Cria um ambiente novo com UMA execução (versão 1) no status pedido; devolve o environment_id.
    private async Task<Guid> SeedRunOnNewEnvironmentAsync(
        TenantScope scope, byte status, byte resultStatus, DateTimeOffset completedAt)
    {
        var environmentId = Guid.NewGuid();
        await SeedEnvironmentAsync(scope, environmentId, "site-" + environmentId.ToString("N")[..8]);
        await SeedRunAsync(scope, environmentId, 1, status, resultStatus, completedAt);
        return environmentId;
    }

    private async Task SeedRunAsync(
        TenantScope scope, Guid environmentId, int version, byte status, byte resultStatus, DateTimeOffset completedAt)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.ev_discovery_runs
                (environment_id, discovery_version, tenant_id, project_id, run_id, status, result_status, result_code,
                 observed_version, discovery_schema_version, configuration_hash, evidence_hash, content_sha256,
                 evidence_path, evidence_size_bytes, correlation_id, started_at_utc, completed_at_utc)
            VALUES
                (@env, @version, @tenant, @project, @run, @status, @resultStatus, 'EV_CODE',
                 '15.1', 1, @configHash, @semanticHash, @contentHash, @path, 1024, @correlation, @completed, @completed);
            """, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = status });
        command.Parameters.Add(new SqlParameter("@resultStatus", SqlDbType.TinyInt) { Value = resultStatus });
        command.Parameters.Add(new SqlParameter("@configHash", SqlDbType.Char, 64) { Value = new string('a', 64) });
        command.Parameters.Add(new SqlParameter("@semanticHash", SqlDbType.Char, 64) { Value = new string('b', 64) });
        command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = new string('c', 64) });
        command.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 400) { Value = $"logical/{environmentId:N}/v{version}" });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@completed", SqlDbType.DateTime2) { Value = completedAt.UtcDateTime });
        await command.ExecuteNonQueryAsync();
    }
}
