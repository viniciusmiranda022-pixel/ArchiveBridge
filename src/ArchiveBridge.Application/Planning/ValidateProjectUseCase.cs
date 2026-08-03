using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.Planning;

/// <summary>Resultado de <see cref="ValidateProjectUseCase"/>: o estado resultante do projeto.</summary>
public sealed record ProjectValidationResult(ProjectStatus Status);

/// <summary>
/// Corpo de execução do comando durável <c>ValidateProject</c> (invocado pelo
/// <c>PlanningCommandProcessor</c> após reivindicar o Job). Valida a integridade da configuração (o
/// hash persistido tem de reproduzir-se a partir da configuração — detecta manipulação de hash) e,
/// se ainda em Draft, submete o projeto para avaliação. É idempotente em retry: só transita a partir
/// de Draft (reexecutar após já ter submetido não duplica efeito).
/// </summary>
public sealed class ValidateProjectUseCase(IProjectStore projects, IClock clock)
{
    private readonly IProjectStore _projects = projects;
    private readonly IClock _clock = clock;

    /// <summary>Executa a validação do projeto do escopo. Quando <paramref name="fence"/> é informado, o efeito é cercado pelo Job.</summary>
    public async Task<ProjectValidationResult> ExecuteAsync(
        TenantScope scope, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null)
    {
        var project = await _projects.GetAsync(scope, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Projeto não encontrado no escopo.");

        if (!string.Equals(
                project.Configuration.ComputeHash().Value,
                project.ConfigurationHash.Value,
                StringComparison.Ordinal))
        {
            throw new PlanningValidationException("Hash de configuração do projeto inconsistente; validação recusada.");
        }

        if (project.Status == ProjectStatus.Draft)
        {
            project.SubmitForAssessment(_clock.UtcNow);
            await _projects.SaveStatusAsync(project, correlation, cancellationToken, fence).ConfigureAwait(false);
        }

        return new ProjectValidationResult(project.Status);
    }
}
