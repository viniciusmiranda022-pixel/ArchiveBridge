// ArchiveBridge Control Plane — API + Portal Operacional (Slice 4A).
//
// Instalação ON-PREMISES (IIS ou Windows Service): sem Azure App Service, sem banco em nuvem, sem SaaS,
// sem comunicação externa além das integrações explicitamente configuradas. Esta fatia entrega apenas
// LEITURA (observabilidade de projetos, ondas, jobs, descoberta EV e evidências) sob autenticação e RBAC;
// nenhuma capacidade do Slice 4B (exportação, PST, Purview, Graph, AzCopy) é executada nem simulada.
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Infrastructure.Persistence;
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

builder.Services.AddSingleton(connectionFactory);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IPortalScopeAccessor, PortalScopeAccessor>();
builder.Services.AddScoped<IPortalUserStore>(_ => new SqlPortalUserStore(applicationConnection));
builder.Services.AddScoped<IPortalSignInAudit>(_ => new SqlPortalSignInAudit(applicationConnection));
builder.Services.AddScoped<IControlPlaneQueries>(_ => new SqlControlPlaneQueries(connectionFactory));

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
