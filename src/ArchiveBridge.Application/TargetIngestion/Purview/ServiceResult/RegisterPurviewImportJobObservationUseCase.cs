using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Registra uma observação transcrita pelo operador sobre o import job do Purview (runbook §25.9 item 75;
/// AB-I6-001 item 5). O plano referenciado deve existir no escopo (anti-IDOR) e a evidência canônica
/// permanece sem drift (mesma guarda de <see cref="PlanPurviewImportJobUseCase"/>). A convergência
/// idempotente de replay e a recusa fail-closed de reassociação de
/// <see cref="Domain.TargetIngestion.Purview.ServiceResult.PurviewProviderOperationId"/> são aplicadas
/// TRANSACIONALMENTE por <see cref="IPurviewImportJobStore.RecordObservationAsync"/> — nunca decididas
/// aqui em duas etapas separadas (races).
/// </summary>
public sealed class RegisterPurviewImportJobObservationUseCase(
    ResolvePurviewMappingEvidenceUseCase evidenceResolver,
    IPurviewMappingCsvStore mappings,
    IPurviewImportJobStore jobs,
    IClock clock)
{
    private readonly ResolvePurviewMappingEvidenceUseCase _evidenceResolver = evidenceResolver;
    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IPurviewImportJobStore _jobs = jobs;
    private readonly IClock _clock = clock;

    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda/plano inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException">Upload/mapping não canônico, drift, ou horário observado fora dos limites plausíveis.</exception>
    /// <exception cref="PurviewImportJobIdentityConflictException">Reassociação de provider ID incompatível (fail-closed).</exception>
    public async Task<PurviewImportJobObservation> ExecuteAsync(
        TenantScope scope,
        WaveId waveId,
        PurviewImportJobName plannedJobName,
        PurviewProviderOperationId providerOperationId,
        PurviewImportJobObservedStatus observedStatus,
        DateTimeOffset observedAtUtc,
        string operatorLabel,
        CancellationToken cancellationToken,
        JobFence? fence = null)
    {
        await PurviewImportJobEvidenceGuard
            .ResolveAndVerifyNoDriftAsync(_evidenceResolver, _mappings, scope, waveId, cancellationToken)
            .ConfigureAwait(false);

        var plan = await _jobs.GetPlanByNameAsync(scope, waveId, plannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException(
                "Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        var now = _clock.UtcNow;
        var observation = PurviewImportJobObservation.Create(
            scope.Tenant, scope.Project, plan.Wave, plan.PlannedJobName, providerOperationId, observedStatus, observedAtUtc, operatorLabel, now);

        return await _jobs.RecordObservationAsync(scope, observation, fence, cancellationToken).ConfigureAwait(false);
    }
}
