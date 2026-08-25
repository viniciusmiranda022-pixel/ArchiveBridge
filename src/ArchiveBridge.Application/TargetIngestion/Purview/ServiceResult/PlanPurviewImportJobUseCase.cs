using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Planeja (ou reaproveita idempotentemente) o import job do Purview de uma onda (AB-I6-001 item 4): exige
/// que upload+mapping canônicos existam sem drift (<see cref="PurviewImportJobEvidenceGuard"/>), então
/// deriva/persiste um <see cref="PurviewImportJobPlan"/> server-side — o valor que o OPERADOR HUMANO deve
/// transcrever manualmente no portal Purview (runbook §25.9 item 67). Nunca cria, valida ou inicia
/// qualquer job no portal (STOP-THE-LINE). Idempotente pela impressão digital da evidência canônica: a
/// MESMA evidência sempre devolve o MESMO plano (nenhuma nova tentativa por replay); evidência
/// REALMENTE diferente produz uma nova tentativa/nome.
/// </summary>
public sealed class PlanPurviewImportJobUseCase(
    ResolvePurviewMappingEvidenceUseCase evidenceResolver,
    IPurviewMappingCsvStore mappings,
    IPurviewImportJobStore jobs,
    IClock clock)
{
    private readonly ResolvePurviewMappingEvidenceUseCase _evidenceResolver = evidenceResolver;
    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IPurviewImportJobStore _jobs = jobs;
    private readonly IClock _clock = clock;

    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda inexistente/fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException">Upload/mapping não canônico, ou mapping publicado divergente da evidência atual (drift).</exception>
    public async Task<PurviewImportJobPlan> ExecuteAsync(
        TenantScope scope, WaveId waveId, string generatedBy, CancellationToken cancellationToken, JobFence? fence = null)
    {
        var check = await PurviewImportJobEvidenceGuard
            .ResolveAndVerifyNoDriftAsync(_evidenceResolver, _mappings, scope, waveId, cancellationToken)
            .ConfigureAwait(false);

        var pending = await _jobs.GetLatestPlanByFingerprintAsync(scope, waveId, check.Fingerprint, cancellationToken).ConfigureAwait(false);
        if (pending is not null)
        {
            // Reaproveitamento idempotente: a MESMA evidência canônica já produziu um plano — devolve-o
            // sem alocar nova tentativa/nome (AB-I6-001 item 10).
            return pending;
        }

        return await _jobs
            .CreatePlanAsync(scope, waveId, check.Fingerprint, generatedBy, _clock.UtcNow, fence, cancellationToken)
            .ConfigureAwait(false);
    }
}
