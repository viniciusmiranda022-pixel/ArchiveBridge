using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Passo 6B — a superfície HTTP/Portal de upload/validação de Mapping CSV, ligada ao SQL Server real e ao
/// backend seguro do Passo 6A (<see cref="ArchiveBridge.Application.Mapping.ValidateMappingCsvUploadUseCase"/>).
/// Prova RBAC server-side, precedência RBAC-antes-do-gate, antiforgery, Presentation Mode zero-efeitos,
/// anti-IDOR, validação de estado da onda, idempotência/replay/conflito, oversized, autoridade server-side
/// do contexto (tenant/projeto/usuário nunca vêm do formulário) e a ausência de bytes brutos persistidos
/// pela aplicação — SEM depender de nenhuma capacidade de exportação/importação (Slice 4B).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aPortalMappingUploadHttpTests(SqlServerFixture fixture)
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private readonly SqlServerFixture _fixture = fixture;

    // ---- RBAC: apenas Operator/Administrator podem submeter (POST HTTP real).

    [Fact]
    public async Task OperatorUploadIsAcceptedAndRedirectsToCanonicalResult()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // 302 (PRG)
        Assert.Contains("validationId=", response.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await CountAttemptsAsync(scope));

        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && e.ActionCode == "mapping.validation.submit" && e.Succeeded && e.Reason == "accepted");
    }

    [Fact]
    public async Task AdministratorUploadIsAccepted()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Administrator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, await CountAttemptsAsync(scope));
    }

    [Theory]
    [InlineData(PortalRoles.Viewer)]
    [InlineData(PortalRoles.Auditor)]
    [InlineData(PortalRoles.Approver)]
    public async Task NonOperatorRolesAreForbiddenAndCreateNoAttempt(string role)
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, role);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // 403 real
        Assert.Equal(0, await CountAttemptsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "forbidden");
    }

    // ---- feature gate: precedido pelo RBAC.

    [Fact]
    public async Task FeatureDisabledReturns503AndCreatesNoAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: false);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using (var page = await client.GetAsync(new Uri("/Mapping/Index", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("indisponível neste deployment", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // 503
        Assert.Equal(0, await CountAttemptsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "feature-disabled");
    }

    [Theory]
    [InlineData(PortalRoles.Viewer)]
    [InlineData(PortalRoles.Auditor)]
    [InlineData(PortalRoles.Approver)]
    public async Task NonOperatorRolesAreForbiddenEvenWhenGateDisabled(string role)
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, role);

        using var factory = CreateFactory(uploadEnabled: false); // gate DESABILITADO
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // 403 — NÃO 503 (RBAC precede o gate)
        Assert.Equal(0, await CountAttemptsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "forbidden");
        Assert.DoesNotContain(events, e => e.Username == username && e.Reason == "feature-disabled");
    }

    [Fact]
    public async Task NonOperatorAuthorizationResponseIsIdenticalRegardlessOfGate()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Viewer);

        HttpStatusCode gateEnabled;
        using (var enabled = CreateFactory(uploadEnabled: true))
        using (var client = enabled.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));
            gateEnabled = response.StatusCode;
        }

        HttpStatusCode gateDisabled;
        using (var disabled = CreateFactory(uploadEnabled: false))
        using (var client = disabled.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));
            gateDisabled = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, gateEnabled);
        Assert.Equal(gateEnabled, gateDisabled);
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    [Theory]
    [InlineData(PortalRoles.Viewer)]
    [InlineData(PortalRoles.Auditor)]
    [InlineData(PortalRoles.Approver)]
    public async Task NonOperatorGetPageDoesNotRevealFeatureGateState(string role)
    {
        var (scope, _) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, role);

        HttpStatusCode gateEnabledStatus;
        string gateEnabledHtml;
        using (var enabled = CreateFactory(uploadEnabled: true))
        using (var client = enabled.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var page = await client.GetAsync(new Uri("/Mapping/Index", UriKind.Relative));
            gateEnabledStatus = page.StatusCode;
            gateEnabledHtml = await page.Content.ReadAsStringAsync();
        }

        HttpStatusCode gateDisabledStatus;
        string gateDisabledHtml;
        using (var disabled = CreateFactory(uploadEnabled: false))
        using (var client = disabled.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var page = await client.GetAsync(new Uri("/Mapping/Index", UriKind.Relative));
            gateDisabledStatus = page.StatusCode;
            gateDisabledHtml = await page.Content.ReadAsStringAsync();
        }

        // O status observável é idêntico com o gate ligado ou desligado.
        Assert.Equal(HttpStatusCode.OK, gateEnabledStatus);
        Assert.Equal(gateEnabledStatus, gateDisabledStatus);

        // Um principal sem RBAC nunca vê o texto que revelaria o estado do feature gate, em NENHUM dos dois casos.
        Assert.DoesNotContain("indisponível neste deployment", gateEnabledHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("indisponível neste deployment", gateDisabledHtml, StringComparison.Ordinal);

        // A mensagem observada é a MESMA (leitura apenas) nos dois casos — o estado do gate é indistinguível.
        Assert.Contains("Acesso de leitura", gateEnabledHtml, StringComparison.Ordinal);
        Assert.Contains("Acesso de leitura", gateDisabledHtml, StringComparison.Ordinal);
        Assert.Contains("Requer o papel Operator ou Administrator", gateEnabledHtml, StringComparison.Ordinal);
        Assert.Contains("Requer o papel Operator ou Administrator", gateDisabledHtml, StringComparison.Ordinal);
    }

    // ---- baseline de autenticação: anônimo nunca chega ao handler.

    [Fact]
    public async Task AnonymousAccessIsChallengedAndCreatesNoAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect()); // sem login: principal anônimo

        using (var page = await client.GetAsync(new Uri("/Mapping/Index", UriKind.Relative)))
        {
            // Baseline do Portal (FallbackPolicy = RequireAuthenticatedUser): challenge para o login.
            Assert.Equal(HttpStatusCode.Found, page.StatusCode);
            Assert.Contains("/Account/Login", page.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        using var multipart = new MultipartFormDataContent
        {
            { new StringContent(wave.Id.Value.ToString("D")), "waveId" },
            { new StringContent("1252"), "contentCodePage" },
            { new StringContent(Guid.NewGuid().ToString("D")), "idempotencyKey" },
        };
        AddFile(multipart, ValidCsv(wave), "mapping.csv");
        using var response = await client.PostAsync(new Uri("/Mapping/Index?handler=ValidateCsv", UriKind.Relative), multipart);

        // O handler nunca executa: a autorização é resolvida no middleware, ANTES do endpoint.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await CountAttemptsAsync(scope)); // zero tentativa custodiada
        Assert.Empty(await SubmitEventsAsync(scope));     // zero auditoria de negócio
    }

    // ---- identidade fail-closed: autenticado, porém sem UserId utilizável ⇒ 403 e zero efeitos.

    [Theory]
    [InlineData(null)]        // claim portal_user_id AUSENTE
    [InlineData("")]          // claim presente e vazia
    [InlineData("not-a-guid")] // claim presente e inválida
    [InlineData("00000000-0000-0000-0000-000000000000")] // Guid.Empty: sintaticamente válido, sem identidade
    public async Task AuthenticatedPrincipalWithoutUsableUserIdIsForbiddenWithZeroEffects(string? userIdClaim)
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var username = "stub_" + Guid.NewGuid().ToString("N");

        // Principal autenticado, com escopo e RBAC de Operator VÁLIDOS — só a identidade é inutilizável.
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(PortalClaims.TenantId, scope.Tenant.Value.ToString()),
            new(PortalClaims.ProjectId, scope.Project.Value.ToString()),
            new(ClaimTypes.Role, PortalRoles.Operator),
        };
        if (userIdClaim is not null)
        {
            claims.Add(new Claim(PortalClaims.UserId, userIdClaim));
        }

        using var factory = CreateFactory(uploadEnabled: true, stubPrincipalClaims: claims);
        using var client = factory.CreateClient(NoRedirect()); // autenticado pelo esquema de teste

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(scope)); // nenhuma tentativa custodiada
        // Sem identidade responsabilizável não há evento de negócio: a recusa precede qualquer store/auditoria.
        Assert.Empty(await SubmitEventsAsync(scope));
    }

    [Fact]
    public async Task AuthenticatedPrincipalWithValidUserIdClaimIsAcceptedProvingTheStubIsNotTheCause()
    {
        // Controle do teste acima: o MESMO esquema de teste, com uma identidade utilizável, é aceito —
        // logo o 403 anterior vem da identidade inválida, não do mecanismo de autenticação usado no teste.
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, _) = await SeedUserAsync(scope, PortalRoles.Operator);
        var userId = await FindUserIdAsync(username);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(PortalClaims.UserId, userId.ToString("D")),
            new(PortalClaims.TenantId, scope.Tenant.Value.ToString()),
            new(PortalClaims.ProjectId, scope.Project.Value.ToString()),
            new(ClaimTypes.Role, PortalRoles.Operator),
        };

        using var factory = CreateFactory(uploadEnabled: true, stubPrincipalClaims: claims);
        using var client = factory.CreateClient(NoRedirect());

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, await CountAttemptsAsync(scope));
    }

    // ---- Presentation Mode: zero efeitos, mesmo com gate habilitado e usuário autorizado.

    [Fact]
    public async Task PresentationModeRefusesUploadAndWritesNothing()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true, presentation: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    // ---- antiforgery.

    [Fact]
    public async Task PostWithoutAntiforgeryTokenIsRejectedAndCreatesNoAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave), includeAntiforgeryToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    [Fact]
    public async Task PostWithInvalidAntiforgeryTokenIsRejectedAndCreatesNoAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        // Token PRESENTE porém forjado (não emitido para este par cookie/identidade): a validação
        // criptográfica falha e o handler nunca executa — recusa idêntica à do token ausente.
        var authentic = await GetAntiforgeryTokenAsync(client, "/Mapping/Index");
        var forged = new string(authentic.Reverse().ToArray());
        Assert.NotEqual(authentic, forged);

        using var multipart = new MultipartFormDataContent
        {
            { new StringContent(wave.Id.Value.ToString("D")), "waveId" },
            { new StringContent("1252"), "contentCodePage" },
            { new StringContent(Guid.NewGuid().ToString("D")), "idempotencyKey" },
            { new StringContent(forged), "__RequestVerificationToken" },
        };
        AddFile(multipart, ValidCsv(wave), "mapping.csv");

        using var response = await client.PostAsync(new Uri("/Mapping/Index?handler=ValidateCsv", UriKind.Relative), multipart);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(scope)); // zero tentativa custodiada
        Assert.Empty(await SubmitEventsAsync(scope));     // zero auditoria de negócio
    }

    // ---- validação de entrada.

    [Fact]
    public async Task MissingFileReturns400AndCreatesNoAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    [Fact]
    public async Task EmptyWaveIdReturns400AndCreatesNoAttempt()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, Guid.Empty, "a,b\n1,2"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    // ---- anti-IDOR.

    [Fact]
    public async Task UploadForWaveInAnotherProjectSameTenantReturns404()
    {
        var operatorScope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(operatorScope), CorrelationId.New(), CancellationToken.None);
        var otherProjectScope = new TenantScope(operatorScope.Tenant, new ProjectId(Guid.NewGuid()));
        var (foreignScope, foreignWave) = await SeedApprovedWaveAsync(otherProjectScope);
        var (username, password) = await SeedUserAsync(operatorScope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, foreignWave.Id.Value, ValidCsv(foreignWave));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountAttemptsAsync(operatorScope));
        Assert.Equal(0, await CountAttemptsAsync(foreignScope));
        var events = await AuditAsync(operatorScope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "not-found-or-not-authorized");
    }

    [Fact]
    public async Task UploadForWaveInAnotherTenantReturns404()
    {
        var operatorScope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(operatorScope), CorrelationId.New(), CancellationToken.None);
        var (_, foreignWave) = await SeedApprovedWaveAsync(SqlServerFixture.NewScope());
        var (username, password) = await SeedUserAsync(operatorScope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, foreignWave.Id.Value, ValidCsv(foreignWave));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // externamente idêntico ao cross-project
        Assert.Equal(0, await CountAttemptsAsync(operatorScope));
    }

    [Fact]
    public async Task ValidationIdFromAnotherScopeReturns404OnGet()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var (foreignScope, foreignWave) = await SeedApprovedWaveAsync();
        var (foreignUsername, foreignPassword) = await SeedUserAsync(foreignScope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);

        Guid foreignValidationId;
        using (var foreignClient = factory.CreateClient(NoRedirect()))
        {
            await LoginAsync(foreignClient, foreignUsername, foreignPassword);
            using var foreignUpload = await PostValidateCsvAsync(foreignClient, foreignWave.Id.Value, ValidCsv(foreignWave));
            foreignValidationId = ExtractValidationId(foreignUpload.Headers.Location!);
        }

        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);
        using var response = await client.GetAsync(new Uri($"/Mapping/Index?validationId={foreignValidationId:D}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- estado da onda.

    [Fact]
    public async Task WaveNotApprovedOrFrozenReturns409AndCreatesNoAttempt()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(_fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None); // Draft
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostValidateCsvAsync(client, wave.Id.Value, "linha,irrelevante\n1,2"u8.ToArray());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); // 409
        Assert.Equal(0, await CountAttemptsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "precondition-failed");
    }

    // ---- idempotência.

    [Fact]
    public async Task SameIdempotencyKeyReplaysSameValidationId()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();
        var csv = ValidCsv(wave);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var first = await PostValidateCsvAsync(client, wave.Id.Value, csv, idempotencyKey: idempotencyKey);
        using var second = await PostValidateCsvAsync(client, wave.Id.Value, csv, idempotencyKey: idempotencyKey);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(ExtractValidationId(first.Headers.Location!), ExtractValidationId(second.Headers.Location!));
        Assert.Equal(1, await CountAttemptsAsync(scope));

        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && e.Succeeded && e.Reason == "accepted");
        Assert.Contains(events, e => e.Username == username && e.Succeeded && e.Reason == "idempotent-replay");
    }

    [Fact]
    public async Task SameKeyDifferentWaveReturns409ConflictAndNoSecondAttempt()
    {
        var (scope, waveA) = await SeedApprovedWaveAsync();
        var waveB = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("b.pst", "b@contoso.com", 10)])));
        var waveStore = Slice2Support.WaveStore(_fixture);
        await waveStore.AddAsync(waveB, CorrelationId.New(), CancellationToken.None);
        await waveStore.SaveStatusAsync(waveB, CorrelationId.New(), CancellationToken.None);
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var first = await PostValidateCsvAsync(client, waveA.Id.Value, ValidCsv(waveA), idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        using var second = await PostValidateCsvAsync(client, waveB.Id.Value, ValidCsv(waveB), idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode); // 409

        Assert.Equal(1, await CountAttemptsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "idempotency-conflict");
    }

    // ---- oversized: o portal permanece bounded; o backend 6A é a autoridade final de tamanho.

    [Fact]
    public async Task OversizedContentReturns413AndCreatesNoAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true, effectiveMaxUploadBytes: 128);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        var oversized = new byte[1024];
        using var response = await PostValidateCsvAsync(client, wave.Id.Value, oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); // 413
        Assert.Equal(0, await CountAttemptsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "payload-too-large");
    }

    // ---- autoridade server-side: campos maliciosos do formulário são IGNORADOS.

    [Fact]
    public async Task MaliciousExtraFormFieldsAreIgnoredScopeAndIdentityAreServerSide()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        var token = await GetAntiforgeryTokenAsync(client, "/Mapping/Index");
        using var multipart = new MultipartFormDataContent
        {
            { new StringContent(wave.Id.Value.ToString("D")), "waveId" },
            { new StringContent("1252"), "contentCodePage" },
            { new StringContent(Guid.NewGuid().ToString("D")), "idempotencyKey" },
            { new StringContent(token), "__RequestVerificationToken" },
            // Campos maliciosos: NÃO devem ser vinculados/persistidos.
            { new StringContent(Guid.NewGuid().ToString("D")), "TenantId" },
            { new StringContent(Guid.NewGuid().ToString("D")), "ProjectId" },
            { new StringContent(Guid.NewGuid().ToString("D")), "UserId" },
            { new StringContent("hacker@evil.example"), "RequestedBy" },
            { new StringContent("Administrator"), "Role" },
        };
        AddFile(multipart, ValidCsv(wave), "mapping.csv");

        using var response = await client.PostAsync(new Uri("/Mapping/Index?handler=ValidateCsv", UriKind.Relative), multipart);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var attempt = await ReadSingleAttemptAsync(scope);
        Assert.Equal(scope.Tenant.Value, attempt.TenantId);   // do principal, não do formulário
        Assert.Equal(scope.Project.Value, attempt.ProjectId); // do principal, não do formulário
        Assert.Equal(username, attempt.RequestedBy);          // User.Identity.Name, não "hacker@evil.example"
        Assert.NotEqual("hacker@evil.example", attempt.RequestedBy);
    }

    // ---- renderização segura do resultado canônico.

    [Fact]
    public async Task ValidResultRendersCanonicalMetadataWithoutRawContent()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var upload = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave), fileName: "very-secret-mailbox-path.csv");
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);

        using var result = await client.GetAsync(upload.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();

        Assert.Contains("Valid", html, StringComparison.Ordinal);
        Assert.Contains(ExtractValidationId(upload.Headers.Location!).ToString(), html, StringComparison.OrdinalIgnoreCase);
        // O nome de arquivo informado pelo cliente nunca é exibido — apenas metadados canônicos.
        Assert.DoesNotContain("very-secret-mailbox-path.csv", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidResultRendersStructuredIssuesWithoutCellValues()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        var invalidCsv = "header.invalid,not,the,real,schema\r\nsecret-mailbox@contoso.com,x\r\n"u8.ToArray();
        using var upload = await PostValidateCsvAsync(client, wave.Id.Value, invalidCsv);
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);

        using var result = await client.GetAsync(upload.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();

        Assert.Contains("Invalid", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-mailbox@contoso.com", html, StringComparison.Ordinal); // sem cell values
    }

    [Fact]
    public async Task RejectedResultIsCustodiedAndRenderedWithoutContentOrFileName()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        // BOM UTF-8 à frente de um CSV íntegro: o backend 6A recebe e custodia o conteúdo, mas não o decodifica
        // (UTF-8 ESTRITO SEM BOM) ⇒ desfecho Rejected. Rejected é RESULTADO de validação, não falha HTTP.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(ValidCsv(wave)).ToArray();
        using var upload = await PostValidateCsvAsync(client, wave.Id.Value, withBom, fileName: "confidential-mailbox-dump.csv");

        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode); // PRG, não 4xx
        var validationId = ExtractValidationId(upload.Headers.Location!);
        Assert.Equal(1, await CountAttemptsAsync(scope)); // a tentativa Rejected É custodiada
        // O desfecho PERSISTIDO é Rejected — não apenas um texto na página.
        Assert.Equal(MappingValidationAttemptOutcome.Rejected, await ReadAttemptOutcomeAsync(scope));

        using var result = await client.GetAsync(upload.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();

        Assert.Contains("Rejected", html, StringComparison.Ordinal);
        Assert.Contains(validationId.ToString(), html, StringComparison.OrdinalIgnoreCase);
        // Somente metadados/issues seguros: nunca o nome de arquivo do cliente nem valores de célula do CSV.
        Assert.DoesNotContain("confidential-mailbox-dump.csv", html, StringComparison.Ordinal);
        Assert.DoesNotContain("u@contoso.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/src/a.pst", html, StringComparison.Ordinal);

        var events = await SubmitEventsAsync(scope);
        Assert.Contains(events, e => e.Succeeded && e.Reason == "accepted" && e.ResourceId == validationId.ToString("N"));
        // A trilha usa o ValidationId canônico como recurso — nunca filename/mailbox/path.
        Assert.DoesNotContain(events, e => e.ResourceId.Contains("confidential", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Presentation Mode no GET: estritamente sintético, nunca misturado com estado real.

    [Fact]
    public async Task PresentationModeGetNeverRendersRealValidationResult()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        // 1) Produz uma tentativa REAL custodiada, fora do modo demonstração.
        Guid realValidationId;
        using (var real = CreateFactory(uploadEnabled: true))
        using (var client = real.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var upload = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));
            Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
            realValidationId = ExtractValidationId(upload.Headers.Location!);
        }

        var realSha = await ReadAttemptShaAsync(scope);

        // 2) O MESMO validationId, agora pedido em modo demonstração, não pode trazer estado real para a tela.
        using var presentation = CreateFactory(uploadEnabled: true, presentation: true);
        using var presentationClient = presentation.CreateClient(NoRedirect());
        await LoginAsync(presentationClient, username, password);

        using var page = await presentationClient.GetAsync(
            new Uri($"/Mapping/Index?validationId={realValidationId:D}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        // Apenas o dataset sintético é exibido, e é honestamente rotulado como demonstração.
        Assert.Contains("Modo demonstração", html, StringComparison.Ordinal);
        Assert.Contains("exemplo sintético", html, StringComparison.Ordinal);
        // Nenhum vestígio da tentativa real: nem o ValidationId canônico, nem o SHA-256 do conteúdo recebido.
        Assert.DoesNotContain(realValidationId.ToString(), html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(realSha, html, StringComparison.OrdinalIgnoreCase);
        // E a demonstração continua read-only: nenhuma tentativa nova foi custodiada pelo GET.
        Assert.Equal(1, await CountAttemptsAsync(scope));
    }

    // ---- nenhum byte bruto do CSV é persistido pela aplicação (evidence root inalterado).

    [Fact]
    public async Task NoRawCsvBytesArePersistedUnderEvidenceRoot()
    {
        var (scope, wave) = await SeedApprovedWaveAsync();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        var before = SnapshotFiles(_fixture.ArtifactRoot);

        using var factory = CreateFactory(uploadEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);
        using var response = await PostValidateCsvAsync(client, wave.Id.Value, ValidCsv(wave));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var after = SnapshotFiles(_fixture.ArtifactRoot);
        Assert.Equal(before, after); // nenhum arquivo novo sob o evidence root da aplicação
    }

    // ============================ infra de teste ============================

    private WebApplicationFactory<Program> CreateFactory(
        bool uploadEnabled,
        bool presentation = false,
        long? effectiveMaxUploadBytes = null,
        IReadOnlyList<Claim>? stubPrincipalClaims = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (stubPrincipalClaims is not null)
            {
                // Só para exercitar principais que o login normal NUNCA emite (identidade ausente/inválida).
                // Substitui apenas o esquema padrão de autenticação; autorização, gate, antiforgery, RBAC e
                // todo o pipeline do Portal permanecem os de produção.
                builder.ConfigureTestServices(services => services
                    .AddAuthentication(StubPrincipalHandler.SchemeName)
                    .AddScheme<StubPrincipalOptions, StubPrincipalHandler>(
                        StubPrincipalHandler.SchemeName, options => options.Claims = stubPrincipalClaims));
            }

            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", _fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", _fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:EvidenceRoot", _fixture.ArtifactRoot);
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
            builder.UseSetting("MappingUpload:Enabled", uploadEnabled ? "true" : "false");
            if (effectiveMaxUploadBytes is { } max)
            {
                builder.UseSetting("MappingUpload:EffectiveMaxUploadBytes", max.ToString(CultureInfo.InvariantCulture));
            }

            builder.UseSetting("PresentationMode:Enabled", presentation ? "true" : "false");
        });

    private static WebApplicationFactoryClientOptions NoRedirect() => new() { AllowAutoRedirect = false };

    private async Task<(string Username, string Password)> SeedUserAsync(TenantScope scope, string role)
    {
        var username = "mu_" + Guid.NewGuid().ToString("N");
        const string password = "Str0ng!pw";
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Mapping " + role, scope.Tenant, scope.Project, Hasher.Hash(password), [role]),
            CancellationToken.None);
        return (username, password);
    }

    private async Task<Guid> FindUserIdAsync(string username)
    {
        var record = await new SqlPortalUserStore(_fixture.ConnectionString)
            .FindByUsernameAsync(username, CancellationToken.None);
        return record!.UserId;
    }

    private async Task<(TenantScope Scope, MigrationWave Wave)> SeedApprovedWaveAsync(TenantScope? scopeOverride = null)
    {
        var scope = scopeOverride ?? SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var wave = Slice2Support.Approve(
            Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)])));
        var waveStore = Slice2Support.WaveStore(_fixture);
        await waveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await waveStore.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave);
    }

    private static byte[] ValidCsv(MigrationWave wave) =>
        MappingCsvGenerator.Generate(wave, new ContentCodePage(1252), MappingPolicy.Default, MappingVersion.Initial, "do", Slice2Support.Now)
            .Document.GetBytes().ToArray();

    private static async Task<HttpResponseMessage> PostValidateCsvAsync(
        HttpClient client,
        Guid waveId,
        byte[]? content,
        string fileName = "mapping.csv",
        int codePage = 1252,
        Guid? idempotencyKey = null,
        bool includeAntiforgeryToken = true)
    {
        var token = includeAntiforgeryToken ? await GetAntiforgeryTokenAsync(client, "/Mapping/Index") : null;
        using var multipart = new MultipartFormDataContent
        {
            { new StringContent(waveId.ToString("D")), "waveId" },
            { new StringContent(codePage.ToString(CultureInfo.InvariantCulture)), "contentCodePage" },
            { new StringContent((idempotencyKey ?? Guid.NewGuid()).ToString("D")), "idempotencyKey" },
        };
        if (token is not null)
        {
            multipart.Add(new StringContent(token), "__RequestVerificationToken");
        }

        if (content is not null)
        {
            AddFile(multipart, content, fileName);
        }

        return await client.PostAsync(new Uri("/Mapping/Index?handler=ValidateCsv", UriKind.Relative), multipart);
    }

    private static void AddFile(MultipartFormDataContent multipart, byte[] content, string fileName)
    {
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        multipart.Add(fileContent, "file", fileName);
    }

    private static Guid ExtractValidationId(Uri location)
    {
        var match = Regex.Match(location.OriginalString, "validationId=([0-9a-fA-F-]{36})");
        Assert.True(match.Success, $"validationId não encontrado em {location}.");
        return Guid.Parse(match.Groups[1].Value);
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Username", username),
            new KeyValuePair<string, string>("Password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]);
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Token antiforgery não encontrado em {path}.");
        return match.Groups[1].Value;
    }

    private async Task<int> CountAttemptsAsync(TenantScope scope) =>
        await CountAsync(scope, "SELECT COUNT(*) FROM dbo.mapping_validation_attempts WHERE project_id = @project;");

    private async Task<int> CountAsync(TenantScope scope, string sql)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<AttemptRow> ReadSingleAttemptAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT tenant_id, project_id, uploaded_by FROM dbo.mapping_validation_attempts WHERE project_id = @project;",
            tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new AttemptRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2).Trim());
    }

    private Task<IReadOnlyList<PortalOperationalAuditEvent>> AuditAsync(TenantScope scope) =>
        new SqlPortalOperationalAudit(_fixture.Factory).RecentAsync(scope, 100, CancellationToken.None);

    /// <summary>Somente os eventos operacionais da ação de submissão de Mapping CSV neste escopo.</summary>
    private async Task<IReadOnlyList<PortalOperationalAuditEvent>> SubmitEventsAsync(TenantScope scope) =>
        (await AuditAsync(scope)).Where(e => e.ActionCode == "mapping.validation.submit").ToList();

    private async Task<MappingValidationAttemptOutcome> ReadAttemptOutcomeAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT outcome FROM dbo.mapping_validation_attempts WHERE project_id = @project;", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        return (MappingValidationAttemptOutcome)Convert.ToInt32(
            await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<string> ReadAttemptShaAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT content_sha256 FROM dbo.mapping_validation_attempts WHERE project_id = @project;",
            tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        return ((string)(await command.ExecuteScalarAsync())!).Trim();
    }

    // Sem I/O de arquivo real: o evidence root é usado por outras fatias (EV) — comparamos apenas a
    // listagem (se o diretório nem existir, ambas as capturas são vazias/iguais).
    private static List<string> SnapshotFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];

    private sealed record AttemptRow(Guid TenantId, Guid ProjectId, string RequestedBy);
}
