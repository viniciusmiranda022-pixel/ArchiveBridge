using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Planning;

/// <summary>Comando de planejamento durável (persistido como TINYINT).</summary>
public enum PlanningCommandKind
{
    /// <summary>Validar a integridade/estado do projeto.</summary>
    ValidateProject = 0,

    /// <summary>Validar a capacidade da onda e (des)bloqueá-la.</summary>
    ValidateWave = 1,

    /// <summary>Gerar (ou reaproveitar) o CSV de mapping da onda.</summary>
    GenerateMappingCsv = 2,

    /// <summary>Congelar a onda aprovada.</summary>
    FreezeWave = 3,
}

/// <summary>
/// Contexto mínimo e versionado de um comando durável (sem PII, sem payload pesado). É gravado junto
/// do Job durável de forma atômica (tabela <c>planning_commands</c>) para que um worker o execute
/// depois de reivindicar o Job.
/// </summary>
public sealed record PlanningCommand(
    PlanningCommandKind Kind,
    TenantScope Scope,
    WaveId? Wave,
    int? ContentCodePage,
    string? GeneratedBy,
    CorrelationId Correlation);

/// <summary>Um comando reivindicado: o Job (com época de fencing) e o contexto a executar.</summary>
public sealed record ClaimedPlanningCommand(ClaimedJob Job, PlanningCommand Command);

/// <summary>
/// Fila durável de comandos de planejamento sobre os Jobs do Slice 1. O enfileiramento é ATÔMICO
/// (Job + contexto em uma única transação): nunca há Job sem operação nem operação sem Job. A
/// reivindicação reutiliza o claim atômico com fencing por época dos Jobs (workload Control).
/// </summary>
public interface IPlanningCommandInbox
{
    /// <summary>Enfileira o comando criando o Job durável e o contexto atomicamente. Retorna o Job.</summary>
    Task<JobId> EnqueueAsync(PlanningCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Reivindica o próximo comando elegível do escopo (Job Control) e carrega o seu contexto;
    /// <see langword="null"/> quando não há trabalho.
    /// </summary>
    Task<ClaimedPlanningCommand?> TryClaimNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken);
}
