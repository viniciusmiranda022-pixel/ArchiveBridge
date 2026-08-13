using System.Security.Claims;
using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.EnterpriseVault;

/// <summary>
/// Enterprise Vault — leitura dos resultados de descoberta e, para Operator/Administrator com o gate
/// habilitado, a PRIMEIRA ação de escrita operacional do Portal: SOLICITAR (enfileirar) uma descoberta
/// READ-ONLY. O POST não executa descoberta — apenas resolve o escopo autenticado e chama
/// <see cref="RequestEvCapabilityDiscoveryUseCase"/>, que enfileira de forma durável e idempotente; o
/// Worker EV processa depois. Tenant/projeto vêm do principal; site/directory/versão/hash/política são
/// resolvidos server-side. A página continua legível pelos papéis de leitura.
/// </summary>
public sealed class IndexModel(
    IControlPlaneQueries queries,
    IPortalScopeAccessor scopeAccessor,
    IAuthorizationService authorization,
    RequestEvCapabilityDiscoveryUseCase requestDiscovery,
    IPortalOperationalAudit operationalAudit,
    EnterpriseVaultDiscoveryPortalOptions discoveryGate,
    IClock clock) : PageModel
{
    /// <summary>Código de ação da trilha operacional para a solicitação de descoberta.</summary>
    public const string ActionCode = "ev.discovery.request";
    private const string ResourceType = "ev-environment";
    private const string DiscoveryOperatorsPolicy = "EvDiscoveryOperators";

    private readonly IControlPlaneQueries _queries = queries;
    private readonly IPortalScopeAccessor _scopeAccessor = scopeAccessor;
    private readonly IAuthorizationService _authorization = authorization;
    private readonly RequestEvCapabilityDiscoveryUseCase _requestDiscovery = requestDiscovery;
    private readonly IPortalOperationalAudit _operationalAudit = operationalAudit;
    private readonly EnterpriseVaultDiscoveryPortalOptions _discoveryGate = discoveryGate;
    private readonly IClock _clock = clock;

    /// <summary>Ambientes EV do escopo, cada um com uma chave de idempotência estável para SEU formulário.</summary>
    public IReadOnlyList<EvEnvironmentRow> Environments { get; private set; } = [];

    /// <summary>Gate do deployment: o Portal pode solicitar descoberta nesta instalação?</summary>
    public bool DiscoveryEnabled { get; private set; }

    /// <summary>Verdadeiro só quando o gate está habilitado E o usuário é Operator/Administrator.</summary>
    public bool CanRequestDiscovery { get; private set; }

    /// <summary>Renderiza a leitura + (quando permitido) os formulários de solicitação.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var scope = _scopeAccessor.Resolve(User);
        var environments = await _queries.ListEvEnvironmentsAsync(scope, cancellationToken).ConfigureAwait(false);

        DiscoveryEnabled = _discoveryGate.Enabled;
        CanRequestDiscovery = DiscoveryEnabled
            && (await _authorization.AuthorizeAsync(User, DiscoveryOperatorsPolicy).ConfigureAwait(false)).Succeeded;

        // Uma chave de idempotência POR formulário (por ambiente), gerada no GET e embutida no <input hidden>:
        // reenvio/double-click do MESMO formulário carrega a MESMA chave ⇒ 1 Job. Formulários de ambientes
        // diferentes têm chaves diferentes.
        Environments = environments.Select(e => new EvEnvironmentRow(e, Guid.NewGuid())).ToList();
        return Page();
    }

    /// <summary>
    /// SOLICITA (enfileira) uma descoberta READ-ONLY. Aceita do navegador SOMENTE
    /// <paramref name="environmentId"/> e <paramref name="idempotencyKey"/>; tenant/projeto vêm do principal,
    /// o solicitante é a identidade autenticada e a correlação é criada no servidor. Nenhum outro campo do
    /// formulário é vinculado. Fail-closed em todo caminho de erro, com auditoria da tentativa.
    /// </summary>
    public async Task<IActionResult> OnPostRequestDiscoveryAsync(
        Guid environmentId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        var correlation = CorrelationId.New();
        var username = User.Identity?.Name ?? string.Empty;

        // Identidade do principal (fail-closed): sem UserId válido não há como responsabilizar — 403.
        // (Sob cookie-auth, Forbid() redirecionaria para AccessDenied/302; a ação de escrita exige 403 real.)
        if (!Guid.TryParse(User.FindFirstValue(PortalClaims.UserId), out var userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Escopo derivado EXCLUSIVAMENTE do principal autenticado; claims inválidas ⇒ fail-closed.
        TenantScope scope;
        try
        {
            scope = _scopeAccessor.Resolve(User);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Autorização RBAC SERVER-SIDE (não confiar em esconder o botão): Operator/Administrator apenas. A
        // autorização PRECEDE o feature gate — um principal sem mandato NUNCA infere o estado do gate pela
        // resposta (403 idêntico com Enabled=true/false). Negativa é auditada como "forbidden".
        var authorized = await _authorization.AuthorizeAsync(User, DiscoveryOperatorsPolicy).ConfigureAwait(false);
        if (!authorized.Succeeded)
        {
            await AuditAsync(scope, userId, username, environmentId, false, "forbidden", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Somente APÓS a autorização: feature gate do deployment. Desabilitado ⇒ nada é enfileirado (nem por
        // usuário autorizado); o "feature-disabled" só é revelado a quem já tem mandato.
        if (!_discoveryGate.Enabled)
        {
            await AuditAsync(scope, userId, username, environmentId, false, "feature-disabled", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Validação de entrada: identificadores GUID não vazios. Inválido ⇒ 400, zero Job.
        if (environmentId == Guid.Empty || idempotencyKey == Guid.Empty)
        {
            await AuditAsync(scope, userId, username, environmentId, false, "invalid-input", correlation, cancellationToken)
                .ConfigureAwait(false);
            return BadRequest();
        }

        EvCapabilityDiscoveryRequestResult result;
        try
        {
            result = await _requestDiscovery.ExecuteAsync(
                new RequestEvCapabilityDiscovery(
                    scope, new EvEnvironmentId(environmentId), username, idempotencyKey, correlation),
                cancellationToken).ConfigureAwait(false);
        }
        catch (EvDiscoveryNotFoundException)
        {
            // Ambiente inexistente/cross-project/cross-tenant são EXTERNAMENTE indistinguíveis (anti-IDOR).
            await AuditAsync(scope, userId, username, environmentId, false, "not-found-or-not-authorized", correlation, cancellationToken)
                .ConfigureAwait(false);
            return NotFound();
        }
        catch (EvDiscoveryIdempotencyConflictException)
        {
            // Mesma chave já vinculada a um comando DIFERENTE: conflito determinístico, nenhum Job novo.
            await AuditAsync(scope, userId, username, environmentId, false, "idempotency-conflict", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status409Conflict);
        }
        catch (ArgumentException)
        {
            await AuditAsync(scope, userId, username, environmentId, false, "invalid-input", correlation, cancellationToken)
                .ConfigureAwait(false);
            return BadRequest();
        }

        // Auditoria de RESULTADO antes do redirect. Se persistir a trilha falhar, a exceção propaga (HTTP 500):
        // o Job durável permanece e o retry com a MESMA chave devolve o MESMO Job (idempotência). NÃO
        // compensamos destrutivamente (enqueue e auditoria não compartilham transação distribuída).
        var reason = result.Replayed ? "idempotent-replay" : "accepted";
        await AuditAsync(scope, userId, username, environmentId, true, reason, correlation, cancellationToken)
            .ConfigureAwait(false);

        // PRG: sucesso/replay redirecionam para os detalhes do Job durável (não renderiza a página após POST).
        return RedirectToPage("/Jobs/Details", new { jobId = result.JobId.Value });
    }

    private Task AuditAsync(
        TenantScope scope,
        Guid userId,
        string username,
        Guid environmentId,
        bool succeeded,
        string reason,
        CorrelationId correlation,
        CancellationToken cancellationToken) =>
        _operationalAudit.RecordAsync(
            scope,
            new PortalOperationalAuditEvent(
                userId,
                username,
                ActionCode,
                ResourceType,
                environmentId.ToString("N"),
                succeeded,
                reason,
                correlation.Value,
                _clock.UtcNow),
            cancellationToken);

    /// <summary>Ambiente EV com a chave de idempotência estável do seu formulário de solicitação.</summary>
    public sealed record EvEnvironmentRow(EvEnvironmentSummary Summary, Guid IdempotencyKey);
}
