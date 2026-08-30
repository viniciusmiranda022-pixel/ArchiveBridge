using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Application.ProductionReadiness;

/// <summary>Consulta o snapshot vigente do Production Readiness Review de um escopo — <see langword="null"/> quando nenhuma revisão ainda foi composta (nunca revela existência cross-tenant, ver <see cref="TenantScope"/>/RLS).</summary>
public sealed record GetProductionReadinessReviewQuery(TenantScope Scope);

/// <summary>
/// Lê o snapshot VIGENTE (não recompõe evidência — ver nota de <see cref="IProductionReadinessReviewStore.GetLatestAsync"/>)
/// e projeta um relatório SANITIZADO (AB-I8-001 escopo item 8). RBAC de leitura permite qualquer papel
/// reconhecido do portal (Viewer/Operator/Approver/Auditor/Administrator) — nunca ator anônimo.
/// </summary>
public sealed class GetProductionReadinessReviewUseCase(IProductionReadinessReviewStore store, IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="ProductionReadinessAuthorizationException">Ator anônimo ou nenhum papel efetivo reconhecido.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<ProductionReadinessReportView?> ExecuteAsync(GetProductionReadinessReviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var authenticatedActor = actorAccessor.Current;
        ProductionReadinessAuthorization.RequireActor(authenticatedActor.ActorId);
        ProductionReadinessAuthorization.EnsureCanRead(authenticatedActor.Roles);

        var snapshot = await store.GetLatestAsync(query.Scope, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : ProductionReadinessReportFormatter.ToReportView(snapshot);
    }
}
