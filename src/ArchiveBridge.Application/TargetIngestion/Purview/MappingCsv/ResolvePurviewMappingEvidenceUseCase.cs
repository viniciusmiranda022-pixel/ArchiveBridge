using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Contracts.WavePartitionBindings;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;

namespace ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Uma tupla canônica resolvida server-side: a entrada planejada, a identidade opaca do vínculo
/// (AB-I5-013), o vínculo/execução físicos e se o precheck canônico comprova o archive ativo/elegível
/// para esta identidade (AB-I5-012 item 6 — "scoped read path for canonical (WaveEntry, binding,
/// execution, UploadVerified evidence) tuples").
/// </summary>
public sealed record PurviewMappingSourceRow(
    WaveEntry Entry, WaveEntryId EntryId, WavePartitionOutputBinding Binding, PartitionExecutionRecord Execution, bool IsArchive);

/// <summary>Evidência completa e já verificada, pronta para o builder puro de Domain gerar o CSV.</summary>
public sealed record PurviewMappingEvidence(
    MigrationWave Wave, IReadOnlyList<PurviewMappingSourceRow> Rows, PurviewUploadAttemptRecord VerifiedAttempt);

/// <summary>
/// Resolve, server-side e a partir SOMENTE de stores canônicos autorizados, a evidência completa exigida
/// para gerar o mapping CSV do Purview de uma onda (AB-I5-012 itens 2-3, AB-I5-013 item 6). O caller nunca
/// escolhe mailbox, PST, path, prefixo remoto ou conteúdo de linha — apenas a onda. Bloqueia fail-closed
/// (<see cref="PurviewMappingCsvGenerationException"/>) quando: a onda não está aprovada/congelada; não há
/// nenhum vínculo canônico de output; uma execução divergiu do vínculo que a referencia; uma entrada de
/// destino não é mais membro da seleção corrente da onda; a identidade de mailbox de uma entrada não foi
/// resolvida; o upload Purview da onda nunca foi solicitado ou ainda não foi verificado
/// (<see cref="PurviewUploadAttemptOutcome.Uploaded"/>); ou a evidência de upload verificada (prefixo/
/// contagem/bytes esperados) diverge do conjunto ATUAL de vínculos canônicos — nesse último caso o upload
/// precisa ser repetido antes de gerar o mapping, nunca o inverso (nunca gera CSV para PSTs não
/// comprovadamente carregados).
/// </summary>
public sealed class ResolvePurviewMappingEvidenceUseCase(
    IWaveStore waves,
    IWavePartitionOutputBindingStore bindings,
    IPartitionExecutionStore executions,
    IPurviewUploadRequestStore uploadRequests,
    IPurviewUploadAttemptStore uploadAttempts,
    IMailboxPrecheckStore prechecks)
{
    private const string SourceNotFoundMessage =
        "Geração de mapping recusada (fail-closed): onda inexistente/fora do escopo autorizado.";

    private readonly IWaveStore _waves = waves;
    private readonly IWavePartitionOutputBindingStore _bindings = bindings;
    private readonly IPartitionExecutionStore _executions = executions;
    private readonly IPurviewUploadRequestStore _uploadRequests = uploadRequests;
    private readonly IPurviewUploadAttemptStore _uploadAttempts = uploadAttempts;
    private readonly IMailboxPrecheckStore _prechecks = prechecks;

    /// <exception cref="PurviewMappingCsvSourceNotFoundException">Onda inexistente/fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewMappingCsvGenerationException">Qualquer invariante de evidência fail-closed listado no tipo.</exception>
    public async Task<PurviewMappingEvidence> ExecuteAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken)
    {
        var waveAggregate = await _waves.GetAsync(scope, wave, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewMappingCsvSourceNotFoundException(SourceNotFoundMessage);

        if (waveAggregate.Status is not (WaveStatus.Approved or WaveStatus.Frozen))
        {
            throw new PurviewMappingCsvGenerationException(
                $"O mapping do Purview só pode ser gerado de uma onda aprovada/congelada; estado atual: {waveAggregate.Status}.");
        }

        var canonicalBindings = await _bindings.ListForWaveAsync(scope, wave, cancellationToken).ConfigureAwait(false);
        if (canonicalBindings.Count == 0)
        {
            throw new PurviewMappingCsvGenerationException("A onda não tem nenhum vínculo canônico de output de particionamento; nada a mapear.");
        }

        var rows = new List<PurviewMappingSourceRow>(canonicalBindings.Count);
        foreach (var binding in canonicalBindings)
        {
            var execution = await _executions.FindCanonicalAsync(scope, binding.Plan, binding.Part, cancellationToken).ConfigureAwait(false);
            if (execution is null || execution.Id != binding.Execution || execution.Artifact != binding.Artifact
                || execution.PartKey != binding.PartKey || execution.OutputHash != binding.OutputHash
                || execution.OutputSizeBytes != binding.OutputSizeBytes)
            {
                throw new PurviewMappingCsvGenerationException(
                    "A execução canônica referenciada por um vínculo divergiu do vínculo (fail-closed).");
            }

            var entry = waveAggregate.Selection.ResolveEntry(wave, binding.Entry)
                ?? throw new PurviewMappingCsvGenerationException(
                    "A entrada de destino de um vínculo não é mais membro da seleção corrente da onda (fail-closed).");

            if (!entry.Archive.IsIdentityResolved)
            {
                throw new PurviewMappingCsvGenerationException(
                    "A identidade de mailbox de uma entrada da onda não foi resolvida server-side (fail-closed).");
            }

            var precheck = await _prechecks.GetLatestAsync(scope, entry.Archive.Identity, cancellationToken).ConfigureAwait(false);
            var isArchive = precheck is { ArchiveStatus: MailboxArchiveStatus.Active };

            rows.Add(new PurviewMappingSourceRow(entry, binding.Entry, binding, execution, isArchive));
        }

        var request = await _uploadRequests.FindCanonicalAsync(scope, wave, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewMappingCsvGenerationException("Nenhum upload Purview foi solicitado para esta onda ainda.");
        var attempt = await _uploadAttempts.GetLatestAsync(scope, request.Id, cancellationToken).ConfigureAwait(false);
        if (attempt is not { Outcome: PurviewUploadAttemptOutcome.Uploaded, Evidence: { } evidence })
        {
            throw new PurviewMappingCsvGenerationException(
                "O upload Purview desta onda ainda não foi verificado (UploadVerified); mapping recusado.");
        }

        var expectedPrefix = PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave);
        if (evidence.RemotePrefix != expectedPrefix)
        {
            throw new PurviewMappingCsvGenerationException(
                "O prefixo remoto da evidência de upload não corresponde ao prefixo canônico da onda (fail-closed).");
        }

        // (AB-I5-015 item 4) Correspondência EXATA 1:1 entre cada binding/execução ATUAL e um item da
        // manifestação verificada — nunca apenas contagem/soma de bytes agregados (item 5: dois conjuntos
        // diferentes de PSTs podem coincidir em quantidade e soma de bytes por acidente). Item ausente,
        // extra, duplicado ou divergente (nome remoto/hash/tamanho) bloqueia a geração inteira.
        var manifestByExecution = evidence.Manifest.ToDictionary(item => item.Execution);
        if (manifestByExecution.Count != rows.Count)
        {
            throw new PurviewMappingCsvGenerationException(
                "A evidência de upload verificada não corresponde EXATAMENTE ao conjunto ATUAL de vínculos canônicos " +
                "da onda (quantidade de itens da manifestação divergente, fail-closed) — repita o upload antes de gerar o mapping.");
        }

        foreach (var row in rows)
        {
            if (!manifestByExecution.TryGetValue(row.Execution.Id, out var manifestItem))
            {
                throw new PurviewMappingCsvGenerationException(
                    "A evidência de upload verificada não cobre um dos vínculos/execuções canônicos ATUAIS da onda " +
                    "(item ausente na manifestação, fail-closed) — repita o upload antes de gerar o mapping.");
            }

            var expectedRemoteName = PurviewRemotePstName.ForPart(row.Execution.Artifact, row.Execution.PartSequence);
            if (!string.Equals(manifestItem.RemoteName.Value, expectedRemoteName.Value, StringComparison.Ordinal)
                || manifestItem.OutputHash != row.Execution.OutputHash
                || manifestItem.ExpectedSizeBytes != row.Execution.OutputSizeBytes)
            {
                throw new PurviewMappingCsvGenerationException(
                    "A evidência de upload verificada diverge do vínculo/execução canônico ATUAL (nome remoto, hash ou " +
                    "tamanho, fail-closed) — repita o upload antes de gerar o mapping.");
            }
        }

        return new PurviewMappingEvidence(waveAggregate, rows, attempt);
    }
}
