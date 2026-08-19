using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>
/// Opções do esquema de autenticação de TESTE: o conjunto EXATO de claims que o principal autenticado
/// deve carregar. Existe para exercitar principais que o fluxo de login normal nunca produz — em
/// particular, um usuário autenticado SEM a claim <c>portal_user_id</c> ou com um valor inválido — e
/// assim provar o comportamento fail-closed do handler HTTP.
/// </summary>
public sealed class StubPrincipalOptions : AuthenticationSchemeOptions
{
    /// <summary>Claims do principal autenticado emitido por este esquema.</summary>
    public IReadOnlyList<Claim> Claims { get; set; } = [];
}

/// <summary>
/// Handler de autenticação de TESTE que sempre autentica com as claims declaradas em
/// <see cref="StubPrincipalOptions"/>. Nunca é registrado pela aplicação — só por
/// <c>WebApplicationFactory.ConfigureTestServices</c> nos testes que precisam de um principal
/// deliberadamente malformado.
/// </summary>
public sealed class StubPrincipalHandler(
    IOptionsMonitor<StubPrincipalOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<StubPrincipalOptions>(options, logger, encoder)
{
    /// <summary>Nome do esquema de teste.</summary>
    public const string SchemeName = "StubPrincipal";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(Options.Claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
