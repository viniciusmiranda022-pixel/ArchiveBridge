// ArchiveBridge Control Plane — API + Portal Operacional (Slice 4A).
//
// Instalação ON-PREMISES (IIS ou Windows Service): sem Azure App Service, sem banco em nuvem, sem SaaS,
// sem comunicação externa além das integrações explicitamente configuradas. Esta fatia entrega superfícies
// de OBSERVABILIDADE/LEITURA (projetos, ondas, jobs, descoberta EV e evidências) e um WRITE-PATH autorizado
// que apenas SOLICITA (enfileira) a descoberta EV — a execução ocorre depois, no Worker EV, e a descoberta
// é READ-ONLY contra o Enterprise Vault. Tudo sob autenticação e RBAC; nenhuma capacidade do Slice 4B
// (exportação, PST, Purview, Graph, AzCopy) é executada nem simulada.
using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.Projects;
using ArchiveBridge.Infrastructure.Time;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>()
    ?? new ControlPlaneOptions();

// Identidades SQL: a da APLICAÇÃO (por tenant, sob RLS) e a de MANUTENÇÃO são obrigatórias e distintas.
var applicationConnection = builder.Configuration.GetConnectionString("Application")
    ?? throw new InvalidOperationException("ConnectionStrings:Application é obrigatória.");
var maintenanceConnection = builder.Configuration.GetConnectionString("Maintenance")
    ?? throw new InvalidOperationException("ConnectionStrings:Maintenance é obrigatória.");
var connectionFactory = new TenantConnectionFactory(applicationConnection, maintenanceConnection);

var evidenceRoot = Path.IsPathRooted(options.EvidenceRoot)
    ? options.EvidenceRoot
    : Path.Combine(builder.Environment.ContentRootPath, options.EvidenceRoot);

builder.Services.AddSingleton(connectionFactory);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IPortalScopeAccessor, PortalScopeAccessor>();
builder.Services.AddSingleton<IEvDiscoveryEvidenceStore>(new FileSystemEvDiscoveryEvidenceStore(evidenceRoot));
builder.Services.AddScoped<IPortalUserStore>(_ => new SqlPortalUserStore(applicationConnection));
builder.Services.AddScoped<IPortalSignInAudit>(_ => new SqlPortalSignInAudit(applicationConnection));
builder.Services.AddScoped<IPortalOperationalAudit>(_ => new SqlPortalOperationalAudit(connectionFactory));
builder.Services.AddScoped<IControlPlaneQueries>(_ => new SqlControlPlaneQueries(connectionFactory));

// Feature gate LOCAL do Portal para a superfície de SOLICITAÇÃO de descoberta (default fail-closed = false).
// Deliberadamente uma opção do Control Plane — sem dependência de ArchiveBridge.Workers.Ev.
var discoveryPortalOptions =
    builder.Configuration.GetSection(EnterpriseVaultDiscoveryPortalOptions.SectionName)
        .Get<EnterpriseVaultDiscoveryPortalOptions>() ?? new EnterpriseVaultDiscoveryPortalOptions();
builder.Services.AddSingleton(discoveryPortalOptions);

// Caso de uso de SOLICITAÇÃO de descoberta READ-ONLY: resolve o contexto autoritativo server-side (site/
// directory, versão/hash de configuração do projeto, versão da política) e ENFILEIRA de forma durável e
// idempotente — NÃO executa descoberta. Usa a identidade da APLICAÇÃO (OpenForTenantAsync + RLS + filtro por
// projeto); a identidade de manutenção NUNCA participa do POST.
builder.Services.AddScoped(serviceProvider =>
{
    var clock = serviceProvider.GetRequiredService<IClock>();
    return new RequestEvCapabilityDiscoveryUseCase(
        new SqlEvEnvironmentCatalog(connectionFactory),
        new SqlProjectStore(connectionFactory, clock),
        new SqlEvDiscoveryCommandInbox(connectionFactory, clock),
        EvDiscoveryPolicy.Default);
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/Account/Login";
        cookie.LogoutPath = "/Account/Logout";
        cookie.AccessDeniedPath = "/Account/Denied";
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.SlidingExpiration = true;
        cookie.Cookie.Name = "ArchiveBridge.Portal";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        // Fora de desenvolvimento o cookie exige HTTPS (Always); em dev local, SameAsRequest não quebra HTTP.
        cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization(authorization =>
{
    // Fail-closed: toda página exige autenticação, exceto as explicitamente anônimas (login/erro/health).
    authorization.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    authorization.AddPolicy("Administrator", policy => policy.RequireRole(PortalRoles.Administrator));
    // A trilha de auditoria é sensível: restrita a quem tem mandato de auditoria/administração.
    authorization.AddPolicy("AuditReaders", policy => policy.RequireRole(PortalRoles.Auditor, PortalRoles.Administrator));
    // Ação de ESCRITA (solicitar descoberta EV): apenas Operator/Administrator. Não protege a PÁGINA
    // /EnterpriseVault (que continua legível pelos papéis de leitura) — protege o handler POST, autorizado
    // server-side via IAuthorizationService, para que uma tentativa negada seja bloqueada E auditada.
    authorization.AddPolicy(
        "EvDiscoveryOperators", policy => policy.RequireRole(PortalRoles.Operator, PortalRoles.Administrator));
});

builder.Services.AddRazorPages(razor =>
{
    razor.Conventions.AllowAnonymousToPage("/Account/Login");
    razor.Conventions.AllowAnonymousToPage("/Account/Denied");
    razor.Conventions.AuthorizeFolder("/Admin", "Administrator");
    razor.Conventions.AuthorizeFolder("/Audit", "AuditReaders");
});

var app = builder.Build();

await BootstrapAsync(app, options).ConfigureAwait(false);

// Fora de desenvolvimento, TLS é obrigatório: HSTS + redirecionamento HTTP→HTTPS (fail-closed em produção).
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Cabeçalhos de segurança + CSP restrita: a página é AUTOCONTIDA (CSS próprio, sem CDN, sem script externo),
// então 'self' cobre tudo — nenhuma origem externa é permitida.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'self'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next().ConfigureAwait(false);
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Liveness (anônimo): prova que o host está no ar. NÃO indica prontidão de nenhuma capacidade de migração.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

// Readiness (anônimo): só declara pronto se o banco obrigatório responder. Fail-closed (503 caso contrário).
app.MapGet("/health/ready", async (CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = new SqlConnection(applicationConnection);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("SELECT 1;", connection);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { status = "ready" });
    }
    catch (SqlException)
    {
        return Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.Run();

// Provisiona o administrador inicial se o portal estiver vazio e a senha de bootstrap estiver definida.
static async Task BootstrapAsync(WebApplication app, ControlPlaneOptions options)
{
    if (options.RunMigrationsAtStartup)
    {
        var migrationsConnection = app.Configuration.GetConnectionString("Migrations")
            ?? throw new InvalidOperationException("ControlPlane:RunMigrationsAtStartup exige ConnectionStrings:Migrations.");
        await new MigrationRunner(migrationsConnection).ApplyAsync(CancellationToken.None).ConfigureAwait(false);
    }

    var bootstrap = options.BootstrapAdmin;
    if (string.IsNullOrEmpty(bootstrap.Password))
    {
        return; // fail-closed: nenhum admin com senha vazia.
    }

    if (!Guid.TryParse(bootstrap.TenantId, out var tenantId) || !Guid.TryParse(bootstrap.ProjectId, out var projectId))
    {
        throw new InvalidOperationException("BootstrapAdmin exige TenantId e ProjectId válidos (GUID).");
    }

    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<IPortalUserStore>();
    if (await users.CountAsync(CancellationToken.None).ConfigureAwait(false) > 0)
    {
        return; // portal já provisionado.
    }

    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    await users.CreateAsync(
        new PortalUserRegistration(
            bootstrap.Username,
            bootstrap.DisplayName,
            new ArchiveBridge.Domain.IdentityAndAccess.TenantId(tenantId),
            new ArchiveBridge.Domain.Projects.ProjectId(projectId),
            hasher.Hash(bootstrap.Password),
            [PortalRoles.Administrator]),
        CancellationToken.None).ConfigureAwait(false);
}

/// <summary>Ponto de entrada exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
