using System.Security.Claims;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.ControlPlane.Composition;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Account;

/// <summary>
/// Autenticação por formulário do portal. Verifica as credenciais contra o store (hash PBKDF2, comparação
/// em tempo constante), recusa usuários desabilitados (fail-closed) e AUDITA toda tentativa — sucesso e
/// falha — sem nunca registrar a senha. Em sucesso, emite o cookie com as claims de identidade e escopo
/// (tenant/projeto) e os papéis (RBAC). Mensagem de erro genérica: não revela se o login existe.
/// </summary>
public sealed class LoginModel(
    IPortalUserStore users,
    IPasswordHasher hasher,
    IPortalSignInAudit audit,
    IClock clock) : PageModel
{
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

    /// <summary>Processa a tentativa de autenticação.</summary>
    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            return await FailAsync("empty-credentials", cancellationToken).ConfigureAwait(false);
        }

        var user = await _users.FindByUsernameAsync(Username, cancellationToken).ConfigureAwait(false);
        if (user is null || !_hasher.Verify(Password, user.Password))
        {
            return await FailAsync("invalid-credentials", cancellationToken).ConfigureAwait(false);
        }

        if (!user.Enabled)
        {
            return await FailAsync("disabled", cancellationToken).ConfigureAwait(false);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(PortalClaims.DisplayName, user.DisplayName),
            new(PortalClaims.TenantId, user.Tenant.Value.ToString()),
            new(PortalClaims.ProjectId, user.Project.Value.ToString()),
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity)).ConfigureAwait(false);

        await _audit.RecordAsync(
            new PortalSignInEvent(user.Username, true, "ok", RemoteAddress(), _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    private async Task<IActionResult> FailAsync(string reason, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(
            new PortalSignInEvent(Username ?? string.Empty, false, reason, RemoteAddress(), _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);
        Error = "Credenciais inválidas.";
        return Page();
    }

    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Page("/Index")!;

    private string? RemoteAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
