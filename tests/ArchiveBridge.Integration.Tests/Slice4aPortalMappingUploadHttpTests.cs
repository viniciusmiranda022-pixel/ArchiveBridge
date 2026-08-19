using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
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
        bool uploadEnabled, bool presentation = false, long? effectiveMaxUploadBytes = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
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

    // Sem I/O de arquivo real: o evidence root é usado por outras fatias (EV) — comparamos apenas a
    // listagem (se o diretório nem existir, ambas as capturas são vazias/iguais).
    private static List<string> SnapshotFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];

    private sealed record AttemptRow(Guid TenantId, Guid ProjectId, string RequestedBy);
}
