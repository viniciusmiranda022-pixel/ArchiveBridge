using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.Planning;

/// <summary>Resultado de <see cref="ValidateProjectUseCase"/>: o Job durável emitido e o estado resultante.</summary>
public sealed record ProjectValidationResult(JobId Job, ProjectStatus Status);

/// <summary>
/// Comando ValidateProject: valida a integridade da configuração do projeto (o hash persistido tem
/// de reproduzir-se a partir da configuração — detecta manipulação de hash) e, se ainda em Draft,
/// submete-o para avaliação. Emite um Job de controle durável (reutiliza a fila do Slice 1) como
/// registro auditável e retriável da operação, correlacionado à mesma <see cref="CorrelationId"/>.
/// </summary>
public sealed class ValidateProjectUseCase(IProjectStore projects, IJobStore jobs, IClock clock)
{
    private readonly IProjectStore _projects = projects;
    private readonly IJobStore _jobs = jobs;
    private readonly IClock _clock = clock;

    /// <summary>Executa a validação do projeto do escopo.</summary>
    public async Task<ProjectValidationResult> ExecuteAsync(
        TenantScope scope, CorrelationId correlation, CancellationToken cancellationToken)
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

        var jobId = await _jobs.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, JobPriority.Normal, correlation), cancellationToken)
            .ConfigureAwait(false);

        if (project.Status == ProjectStatus.Draft)
        {
            project.SubmitForAssessment(_clock.UtcNow);
            await _projects.SaveStatusAsync(project, correlation, cancellationToken).ConfigureAwait(false);
        }

        return new ProjectValidationResult(jobId, project.Status);
    }
}
