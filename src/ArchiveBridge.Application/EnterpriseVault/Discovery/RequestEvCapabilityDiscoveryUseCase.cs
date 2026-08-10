using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>
/// Solicitação de descoberta vinda do plano de controle. O cliente informa APENAS o escopo autenticado, o
/// ambiente-alvo, quem solicita (identidade autenticada), a chave de idempotência e a correlação. Site,
/// directory server, versão/hash de configuração e versão da política NUNCA vêm do cliente.
/// </summary>
public sealed record RequestEvCapabilityDiscovery(
    TenantScope Scope,
    EvEnvironmentId EnvironmentId,
    string RequestedBy,
    Guid IdempotencyKey,
    CorrelationId Correlation);

/// <summary>Resultado da solicitação: o Job durável criado (ou reaproveitado por idempotência).</summary>
public sealed record EvCapabilityDiscoveryRequestResult(JobId JobId, bool Created, bool Replayed);

/// <summary>
/// Caso de uso de SOLICITAÇÃO de descoberta READ-ONLY de capacidades EV. NÃO executa PowerShell nem espera
/// o resultado: resolve o contexto autoritativo no servidor e ENFILEIRA de forma durável e idempotente,
/// devolvendo o <see cref="JobId"/>. O worker processa depois. Toda a autoridade (site/directory, versão/hash
/// de configuração do projeto, versão da política) é resolvida server-side sob o escopo autenticado; um
/// ambiente fora do escopo é indistinguível de inexistente (anti-IDOR) via
/// <see cref="EvDiscoveryNotFoundException"/>.
/// </summary>
public sealed class RequestEvCapabilityDiscoveryUseCase(
    IEvEnvironmentCatalog environments,
    IProjectStore projects,
    IEvDiscoveryCommandInbox inbox,
    EvDiscoveryPolicy policy)
{
    private const int RequestedByMaxLength = 200;

    private readonly IEvEnvironmentCatalog _environments = environments;
    private readonly IProjectStore _projects = projects;
    private readonly IEvDiscoveryCommandInbox _inbox = inbox;
    private readonly EvDiscoveryPolicy _policy = policy;

    /// <summary>Resolve o contexto server-side e enfileira o comando idempotente; devolve o Job.</summary>
    public async Task<EvCapabilityDiscoveryRequestResult> ExecuteAsync(
        RequestEvCapabilityDiscovery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(request));
        }

        // Autoridade do ambiente: resolvido no SQL sob o escopo. Fora do escopo ⇒ indistinguível de inexistente.
        var environment = await _environments.GetAsync(request.Scope, request.EnvironmentId, cancellationToken).ConfigureAwait(false)
            ?? throw new EvDiscoveryNotFoundException("Ambiente de descoberta não encontrado no escopo.");

        // Contexto do projeto resolvido server-side (versão/hash correntes) — nunca fornecido pelo cliente.
        var project = await _projects.GetAsync(request.Scope, cancellationToken).ConfigureAwait(false)
            ?? throw new EvDiscoveryNotFoundException("Projeto não encontrado no escopo.");

        var context = new EvDiscoveryCommandContext(
            EvDiscoveryCommandContext.CurrentSchemaVersion,
            project.ConfigurationVersion.Value,
            project.ConfigurationHash,
            _policy.PolicyVersion);

        var command = new EvDiscoveryCommand(
            request.Scope,
            environment.EnvironmentId,
            environment.SiteName,
            environment.DirectoryServer,
            SanitizeRequestedBy(request.RequestedBy),
            request.Correlation,
            context);

        var enqueued = await _inbox.EnqueueIdempotentAsync(command, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        return new EvCapabilityDiscoveryRequestResult(enqueued.JobId, enqueued.Created, enqueued.Replayed);
    }

    // A identidade vem do principal autenticado; ainda assim é higienizada (sem controle/whitespace de borda)
    // e limitada ao tamanho da coluna, fail-closed.
    private static string SanitizeRequestedBy(string requestedBy)
    {
        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            throw new ArgumentException("O solicitante (identidade autenticada) é obrigatório.", nameof(requestedBy));
        }

        var cleaned = new string(requestedBy.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            throw new ArgumentException("O solicitante é inválido após higienização.", nameof(requestedBy));
        }

        return cleaned.Length > RequestedByMaxLength ? cleaned[..RequestedByMaxLength] : cleaned;
    }
}
