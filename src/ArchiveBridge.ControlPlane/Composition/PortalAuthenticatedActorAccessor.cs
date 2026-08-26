using System.Security.Claims;
using ArchiveBridge.Contracts.Abstractions;
using Microsoft.AspNetCore.Http;

namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Resolve o <see cref="AuthenticatedActor"/> do workflow de disposition de exceções de reconciliação
/// (AB-I6-012) a partir do MESMO mecanismo de identidade/papéis já aceito pelo portal (as claims emitidas
/// em <c>Login.cshtml.cs</c>: <see cref="PortalClaims.UserId"/> para identidade, <see cref="ClaimTypes.Role"/>
/// para papéis) — nunca de um valor fornecido pelo payload da requisição. Mesmo padrão fail-closed de
/// <see cref="PortalScopeAccessor"/>: sem principal autenticado válido, lança em vez de retornar um ator
/// sintético/anônimo.
/// </summary>
public sealed class PortalAuthenticatedActorAccessor(IHttpContextAccessor httpContextAccessor) : IAuthenticatedActorAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    /// <inheritdoc />
    public AuthenticatedActor Current
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                throw new InvalidOperationException("Nenhum principal autenticado no contexto atual (fail-closed).");
            }

            var actorId = user.FindFirstValue(PortalClaims.UserId);
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new InvalidOperationException("Principal autenticado sem identidade de usuário válida (fail-closed).");
            }

            var roles = user.FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .ToArray();

            return new AuthenticatedActor(actorId, roles);
        }
    }
}
