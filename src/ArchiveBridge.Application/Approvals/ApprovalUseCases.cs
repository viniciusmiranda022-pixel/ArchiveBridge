using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Approvals;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Approvals;

/// <summary>Resultado de uma decisão de aprovação de projeto.</summary>
public sealed record ProjectApprovalResult(ProjectStatus Status);

/// <summary>Resultado de uma decisão de aprovação de onda.</summary>
public sealed record WaveApprovalResult(WaveStatus Status);

/// <summary>
/// Comando de decisão (aprovar/rejeitar): carrega a VERSÃO corrente do sujeito (a decisão nunca se
/// aplica a outra versão), o responsável (nunca anônimo), a correlação e notas sem PII.
/// </summary>
public sealed record ApprovalDecisionCommand(
    TenantScope Scope, int SubjectVersion, string ApprovedBy, CorrelationId Correlation, string? Notes);

internal static class ApprovalGuards
{
    public static string RequireApprover(string approvedBy)
    {
        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new PlanningValidationException("Aprovação anônima não é permitida (responsável obrigatório).");
        }

        return approvedBy.Trim();
    }

    public static void EnsureCurrentVersion(int expected, int current)
    {
        if (expected != current)
        {
            throw new PlanningValidationException(
                "A decisão refere-se a uma versão que não é a corrente do sujeito; recusada (fail-closed).");
        }
    }
}

/// <summary>Aprova um projeto (ReadyForAssessment → Approved) e registra a decisão atomicamente.</summary>
public sealed class ApproveProjectUseCase(IProjectStore projects, IClock clock)
{
    private readonly IProjectStore _projects = projects;
    private readonly IClock _clock = clock;

    /// <summary>Executa a aprovação do projeto do escopo.</summary>
    public async Task<ProjectApprovalResult> ExecuteAsync(ApprovalDecisionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var approvedBy = ApprovalGuards.RequireApprover(command.ApprovedBy);
        var project = await _projects.GetAsync(command.Scope, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Projeto não encontrado no escopo.");
        ApprovalGuards.EnsureCurrentVersion(command.SubjectVersion, project.ConfigurationVersion.Value);

        var now = _clock.UtcNow;
        project.Approve(now);
        var approval = new ApprovalRecord(
            ApprovalSubjectType.Project, project.Id.Value, project.ConfigurationVersion.Value,
            ApprovalDecision.Approved, approvedBy, now, command.Correlation, command.Notes);
        await _projects.SaveStatusWithApprovalAsync(project, approval, cancellationToken).ConfigureAwait(false);

        return new ProjectApprovalResult(project.Status);
    }
}

/// <summary>Rejeita um projeto (ReadyForAssessment → Blocked) e registra a decisão atomicamente.</summary>
public sealed class RejectProjectUseCase(IProjectStore projects, IClock clock)
{
    private readonly IProjectStore _projects = projects;
    private readonly IClock _clock = clock;

    /// <summary>Executa a rejeição do projeto do escopo.</summary>
    public async Task<ProjectApprovalResult> ExecuteAsync(ApprovalDecisionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var approvedBy = ApprovalGuards.RequireApprover(command.ApprovedBy);
        var project = await _projects.GetAsync(command.Scope, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Projeto não encontrado no escopo.");
        ApprovalGuards.EnsureCurrentVersion(command.SubjectVersion, project.ConfigurationVersion.Value);

        var now = _clock.UtcNow;
        project.Block(now);
        var approval = new ApprovalRecord(
            ApprovalSubjectType.Project, project.Id.Value, project.ConfigurationVersion.Value,
            ApprovalDecision.Rejected, approvedBy, now, command.Correlation, command.Notes);
        await _projects.SaveStatusWithApprovalAsync(project, approval, cancellationToken).ConfigureAwait(false);

        return new ProjectApprovalResult(project.Status);
    }
}

/// <summary>
/// Aprova uma onda (ReadyForApproval → Approved). Não é possível aprovar com validações pendentes
/// (o estado só chega a ReadyForApproval após elas) nem em versão diferente da corrente.
/// </summary>
public sealed class ApproveWaveUseCase(IWaveStore waves, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly IClock _clock = clock;

    /// <summary>Executa a aprovação da onda.</summary>
    public async Task<WaveApprovalResult> ExecuteAsync(
        WaveId waveId, ApprovalDecisionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var approvedBy = ApprovalGuards.RequireApprover(command.ApprovedBy);
        var wave = await _waves.GetAsync(command.Scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");
        ApprovalGuards.EnsureCurrentVersion(command.SubjectVersion, wave.Version.Value);

        var now = _clock.UtcNow;
        wave.Approve(approvedBy, now);
        var approval = new ApprovalRecord(
            ApprovalSubjectType.Wave, wave.Id.Value, wave.Version.Value,
            ApprovalDecision.Approved, approvedBy, now, command.Correlation, command.Notes);
        await _waves.SaveStatusWithApprovalAsync(wave, approval, cancellationToken).ConfigureAwait(false);

        return new WaveApprovalResult(wave.Status);
    }
}

/// <summary>Rejeita uma onda (ReadyForApproval → Draft) e registra a decisão atomicamente.</summary>
public sealed class RejectWaveUseCase(IWaveStore waves, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly IClock _clock = clock;

    /// <summary>Executa a rejeição da onda.</summary>
    public async Task<WaveApprovalResult> ExecuteAsync(
        WaveId waveId, ApprovalDecisionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var approvedBy = ApprovalGuards.RequireApprover(command.ApprovedBy);
        var wave = await _waves.GetAsync(command.Scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");
        ApprovalGuards.EnsureCurrentVersion(command.SubjectVersion, wave.Version.Value);

        var now = _clock.UtcNow;
        wave.Reject();
        var approval = new ApprovalRecord(
            ApprovalSubjectType.Wave, wave.Id.Value, wave.Version.Value,
            ApprovalDecision.Rejected, approvedBy, now, command.Correlation, command.Notes);
        await _waves.SaveStatusWithApprovalAsync(wave, approval, cancellationToken).ConfigureAwait(false);

        return new WaveApprovalResult(wave.Status);
    }
}
