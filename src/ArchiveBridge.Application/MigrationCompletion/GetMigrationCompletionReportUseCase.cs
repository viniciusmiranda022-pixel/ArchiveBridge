using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.MigrationCompletion;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Application.MigrationCompletion;

/// <summary>Consulta a avaliação de encerramento vigente de um escopo — <see langword="null"/> quando nenhuma ainda composta (nunca revela existência cross-tenant).</summary>
public sealed record GetMigrationCompletionReportQuery(TenantScope Scope);

/// <summary>
/// Lê a avaliação VIGENTE (não recompõe critérios) e projeta um relatório SANITIZADO (AB-I8-010, escopo
/// obrigatório item 12). RBAC de leitura permite qualquer papel reconhecido do portal — nunca ator anônimo.
/// </summary>
public sealed class GetMigrationCompletionReportUseCase(
    IMigrationCompletionAssessmentStore assessmentStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="MigrationCompletionAuthorizationException">Ator anônimo ou nenhum papel efetivo reconhecido.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<MigrationCompletionReportView?> ExecuteAsync(GetMigrationCompletionReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var authenticatedActor = actorAccessor.Current;
        MigrationCompletionAuthorization.RequireActor(authenticatedActor.ActorId);
        MigrationCompletionAuthorization.EnsureCanRead(authenticatedActor.Roles);

        var assessment = await assessmentStore.GetLatestAsync(query.Scope, cancellationToken).ConfigureAwait(false);
        if (assessment is null)
        {
            return null;
        }

        return MigrationCompletionReportFormatter.ToReportView(assessment, isCurrent: true, clock.UtcNow);
    }
}
