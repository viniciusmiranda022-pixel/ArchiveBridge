using System.Security.Claims;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Infrastructure.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Account;

/// <summary>
/// Autenticação por formulário do portal. Verifica as credenciais contra o store (hash PBKDF2, comparação
/// em tempo constante), recusa usuários desabilitados (fail-closed) e AUDITA toda tentativa — sucesso e
/// falha — com escopo (tenant/projeto/usuário quando conhecido) e correlação, sem nunca registrar a senha.
/// Quando o usuário não existe, verifica um hash dummy para não revelar por TEMPO a inexistência do login
/// (a mensagem já é genérica). Em sucesso, emite o cookie com as claims de identidade, escopo e papéis.
/// </summary>
public sealed class LoginModel(
    IPortalUserStore users,
    IPasswordHasher hasher,
    IPortalSignInAudit audit,
    IClock clock) : PageModel
{
    // Hash dummy no MESMO formato/custo do de produção: verificar contra ele custa uma derivação PBKDF2,
    // equalizando o tempo da resposta quando o usuário não existe versus quando existe com senha errada.
    private static readonly PortalPasswordHash DummyHash =
        new Pbkdf2PasswordHasher().Hash("timing-equalization-placeholder");

    private readonly IPortalUserStore _users = users;
    private readonly IPasswordHasher _hasher = hasher;
    private readonly IPortalSignInAudit _audit = audit;
    private readonly IClock _clock = clock;

    /// <summary>Login informado.</summary>
    [BindProperty]
    public string Username { get; set; } = string.Empty;

    /// <summary>Senha informada (nunca persistida nem registrada).</summary>
    [BindProperty]
    public string Password { get; set; } = string.Empty;

    /// <summary>Mensagem de erro genérica exibida em falha.</summary>
    public string? Error { get; private set; }

    /// <summary>Exibe o formulário de login.</summary>
    public void OnGet()
    {
    }

    /// <summary>
    /// Processa a tentativa de autenticação. Rate limiting (Passo 8): protege contra força bruta —
    /// aplicado por <see cref="ArchiveBridge.ControlPlane.Composition.SensitiveOperationRateLimitingMiddleware"/>
    /// (política "login", ANTES deste handler ser alcançado).
    /// </summary>
    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            return await FailAsync(null, "empty-credentials", correlationId, cancellationToken).ConfigureAwait(false);
        }

        var user = await _users.FindByUsernameAsync(Username, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _hasher.Verify(Password, DummyHash); // equaliza o custo; não revela inexistência por tempo
            return await FailAsync(null, "invalid-credentials", correlationId, cancellationToken).ConfigureAwait(false);
        }

        if (!_hasher.Verify(Password, user.Password))
        {
            return await FailAsync(user, "invalid-credentials", correlationId, cancellationToken).ConfigureAwait(false);
        }

        if (!user.Enabled)
        {
            return await FailAsync(user, "disabled", correlationId, cancellationToken).ConfigureAwait(false);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(PortalClaims.UserId, user.UserId.ToString()),
            new(PortalClaims.DisplayName, user.DisplayName),
            new(PortalClaims.TenantId, user.Tenant.Value.ToString()),
            new(PortalClaims.ProjectId, user.Project.Value.ToString()),
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity)).ConfigureAwait(false);

        await _audit.RecordAsync(
            new PortalSignInEvent(
                user.Tenant.Value, user.Project.Value, user.UserId, user.Username, true, "ok",
                RemoteAddress(), correlationId, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    private async Task<IActionResult> FailAsync(
        PortalUserRecord? user, string reason, Guid correlationId, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(
            new PortalSignInEvent(
                user?.Tenant.Value, user?.Project.Value, user?.UserId, Username ?? string.Empty, false, reason,
                RemoteAddress(), correlationId, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        Error = "Credenciais inválidas.";
        return Page();
    }

    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Page("/Index")!;

    private string? RemoteAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
