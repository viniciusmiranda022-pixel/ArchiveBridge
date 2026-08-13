using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Account;

/// <summary>Encerra a sessão (apenas via POST, protegido por antiforgery) e volta ao login.</summary>
public sealed class LogoutModel : PageModel
{
    /// <summary>Sai da sessão do portal.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        return RedirectToPage("/Account/Login");
    }
}
