using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.GoLive;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Application.GoLive;

/// <summary>Consulta a decisão de go-live vigente de um escopo — <see langword="null"/> quando nenhuma ainda registrada (nunca revela existência cross-tenant).</summary>
public sealed record GetGoLiveReportQuery(TenantScope Scope);

/// <summary>
/// Lê a decisão VIGENTE (não reavalia — cada decisão já foi persistida por <see cref="AuthorizeGoLiveUseCase"/>)
/// e projeta um relatório SANITIZADO (AB-I8-010, escopo obrigatório item 12). RBAC de leitura permite qualquer
/// papel reconhecido do portal — nunca ator anônimo.
/// </summary>
public sealed class GetGoLiveReportUseCase(
    IGoLiveAuthorizationStore authorizationStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="GoLiveAuthorizationException">Ator anônimo ou nenhum papel efetivo reconhecido.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<GoLiveReportView?> ExecuteAsync(GetGoLiveReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var authenticatedActor = actorAccessor.Current;
        GoLiveAuthorization.RequireActor(authenticatedActor.ActorId);
        GoLiveAuthorization.EnsureCanRead(authenticatedActor.Roles);

        var decision = await authorizationStore.GetLatestAsync(query.Scope, cancellationToken).ConfigureAwait(false);
        if (decision is null)
        {
            return null;
        }

        return GoLiveReportFormatter.ToReportView(decision, isCurrent: true, clock.UtcNow);
    }
}
