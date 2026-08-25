using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Importa uma versão do validation report / service result do Purview para um plano de import job já
/// existente (AB-I6-001 itens 6-10). O conteúdo é tratado como ENTRADA HOSTIL do início ao fim: nunca
/// confia em nome/path/extensão do chamador, é parseado de forma estrita/bounded pelo Domain
/// (<see cref="PurviewServiceResultReportParser"/>) e correlacionado 1:1 com a cadeia canônica ATUAL da
/// onda (<see cref="PurviewServiceResultCorrelation"/>) — nunca por ordem/posição/nome de arquivo.
/// Idempotente pelo hash do conteúdo bruto: o MESMO relatório (byte a byte) sempre devolve a MESMA
/// versão, sem reparsear/recorrelacionar (item 10); conteúdo realmente diferente produz uma nova versão.
/// </summary>
public sealed class ImportPurviewServiceResultReportUseCase(
    ResolvePurviewMappingEvidenceUseCase evidenceResolver,
    IPurviewMappingCsvStore mappings,
    IPurviewImportJobStore jobs,
    IPurviewServiceResultReportStore reports,
    IClock clock)
{
    private readonly ResolvePurviewMappingEvidenceUseCase _evidenceResolver = evidenceResolver;
    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IPurviewImportJobStore _jobs = jobs;
    private readonly IPurviewServiceResultReportStore _reports = reports;
    private readonly IClock _clock = clock;

    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda/plano inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException">Upload/mapping não canônico, ou drift desde o planejamento/última observação.</exception>
    /// <exception cref="PurviewServiceResultParsingException">Relatório malformado/oversized/encoding inválido (fail-closed).</exception>
    /// <exception cref="PurviewServiceResultCorrelationException">Item desconhecido/duplicado ou completude declarada não cumprida.</exception>
    public async Task<PurviewServiceResultReportEvidence> ExecuteAsync(
        TenantScope scope,
        WaveId waveId,
        PurviewImportJobName plannedJobName,
        ReadOnlyMemory<byte> rawReportBytes,
        string uploadedBy,
        CancellationToken cancellationToken,
        JobFence? fence = null)
    {
        _ = await _jobs.GetPlanByNameAsync(scope, waveId, plannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException(
                "Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        var contentHash = DeterministicHash.ComputeBytes(rawReportBytes.Span);
        var existing = await _reports.GetByContentHashAsync(scope, waveId, plannedJobName, contentHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Replay idempotente: o MESMO conteúdo bruto já foi importado — nunca reparseia/recorrelaciona
            // (item 10), apenas devolve a evidência já persistida.
            return existing;
        }

        var check = await PurviewImportJobEvidenceGuard
            .ResolveAndVerifyNoDriftAsync(_evidenceResolver, _mappings, scope, waveId, cancellationToken)
            .ConfigureAwait(false);

        var parseResult = PurviewServiceResultReportParser.Parse(rawReportBytes);
        _ = PurviewServiceResultCorrelation.Correlate(check.CanonicalRemoteNames, parseResult.Rows, parseResult.DeclaredTotalRows.HasValue);

        return await _reports.PersistAsync(
            scope, waveId, plannedJobName, rawReportBytes, parseResult.Rows, parseResult.DeclaredTotalRows, uploadedBy, _clock.UtcNow, fence,
            cancellationToken).ConfigureAwait(false);
    }
}
