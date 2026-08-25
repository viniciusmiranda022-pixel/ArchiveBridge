using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Resultado de <see cref="GeneratePurviewMappingCsvUseCase"/>: o artefato, a evidência de versão
/// persistida e se houve regeneração (falso quando o retry reaproveitou uma versão utilizável idêntica).
/// </summary>
public sealed record PurviewMappingGenerationOutcome(PurviewMappingDocument Document, PurviewMappingCsvVersion Version, bool Regenerated);

/// <summary>
/// Orquestra a geração (ou reaproveitamento idempotente) do mapping CSV do Purview de uma onda
/// (AB-I5-012). Resolve a evidência canônica via <see cref="ResolvePurviewMappingEvidenceUseCase"/> (o
/// caller nunca escolhe mailbox/PST/path/prefixo — item 2), gera o documento com o builder puro de Domain
/// e persiste com o MESMO protocolo recuperável em duas fases do módulo genérico do Slice 2 (item 8):
/// StageAsync (fora do SQL) → ReserveAsync (transação curta) → PublishAsync (rename atômico, fora do SQL)
/// → FinalizeAsync (transação curta, valida o bundle publicado ANTES de abrir a transação). É idempotente
/// pela impressão digital COMPLETA de geração — só reaproveita quando toda a evidência (vínculos,
/// execuções, mailbox/archive, upload verificado) coincide; qualquer mudança real produz nova versão.
/// </summary>
public sealed class GeneratePurviewMappingCsvUseCase(
    ResolvePurviewMappingEvidenceUseCase evidenceResolver, IPurviewMappingCsvStore mappings, IMappingArtifactStore artifacts, IClock clock)
{
    private readonly ResolvePurviewMappingEvidenceUseCase _evidenceResolver = evidenceResolver;
    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IMappingArtifactStore _artifacts = artifacts;
    private readonly IClock _clock = clock;

    /// <summary>Gera (ou reaproveita) a versão do mapping do Purview da onda. Quando <paramref name="fence"/> é informado, o efeito é cercado pelo Job.</summary>
    /// <exception cref="PurviewMappingCsvSourceNotFoundException">Onda inexistente/fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewMappingCsvGenerationException">Qualquer invariante de evidência ou geração fail-closed.</exception>
    public async Task<PurviewMappingGenerationOutcome> ExecuteAsync(
        TenantScope scope, WaveId waveId, string generatedBy, CancellationToken cancellationToken, JobFence? fence = null)
    {
        var evidence = await _evidenceResolver.ExecuteAsync(scope, waveId, cancellationToken).ConfigureAwait(false);
        var targetRootFolder = evidence.Wave.TargetRootFolder;

        // Ordem de entrada IRRELEVANTE: PurviewMappingCsvGenerator.Generate(...) canonicaliza por Name
        // (Ordinal) antes de serializar/calcular o fingerprint (AB-I5-016) — nunca dependa de
        // CreatedAtUtc aqui, que pode empatar entre bindings persistidos em DATETIME2(3) sem ordem
        // relativa garantida pelo SQL Server.
        var rows = evidence.Rows
            .Select(row => BuildRow(scope, waveId, targetRootFolder, row))
            .ToList();

        var result = PurviewMappingCsvGenerator.Generate(
            waveId, evidence.Wave.Project, targetRootFolder, rows, evidence.VerifiedAttempt.IdentityHash,
            MappingVersion.Initial, generatedBy, _clock.UtcNow);
        var fingerprint = result.Evidence.Fingerprint;

        var usable = await _mappings.GetUsableAsync(scope, waveId, cancellationToken).ConfigureAwait(false);
        if (usable is { } current && current.Fingerprint == fingerprint)
        {
            // Reaproveitamento idempotente: devolve o artefato EXATO daquela versão, nunca o documento
            // recém-gerado (mesmo que o conteúdo lógico coincida byte a byte).
            var content = await _artifacts
                .GetAsync(new MappingArtifactDescriptor(scope, waveId, current.Version), cancellationToken).ConfigureAwait(false)
                ?? throw new PurviewMappingCsvGenerationException(
                    "Versão utilizável sem artefato persistido; evidência inconsistente (fail-closed).");
            var persistedDocument = PurviewMappingDocument.FromPersisted(content.Bytes, current.RowCount, current.ContentSha256);
            return new PurviewMappingGenerationOutcome(persistedDocument, current, Regenerated: false);
        }

        // Protocolo recuperável, SEM I/O de filesystem sob transação SQL. Uma reserva PENDENTE da mesma
        // impressão digital (queda entre reservar e finalizar) é recuperada aqui em vez de reservar uma
        // nova versão — a mesma geração republica (idempotente) e finaliza.
        var pending = await _mappings.GetPendingByFingerprintAsync(scope, waveId, fingerprint, cancellationToken).ConfigureAwait(false);

        var staging = await _artifacts.StageAsync(result.Document.Bytes, result.Document.ContentSha256, cancellationToken).ConfigureAwait(false);
        try
        {
            var reservation = pending
                ?? await _mappings.ReserveAsync(scope, result, staging.SizeBytes, fence, cancellationToken).ConfigureAwait(false);
            var descriptor = new MappingArtifactDescriptor(scope, waveId, reservation.Version);

            // Publica o CONJUNTO imutável FORA de qualquer transação SQL (rename atômico do diretório).
            await _artifacts.PublishAsync(staging, descriptor, cancellationToken).ConfigureAwait(false);

            var persisted = await _mappings.FinalizeAsync(
                scope,
                reservation,
                fence,
                async token =>
                {
                    _ = await _artifacts.GetAsync(descriptor, token).ConfigureAwait(false)
                        ?? throw new PurviewMappingCsvGenerationException(
                            "Artefato publicado ausente/inválido na finalização (fail-closed).");
                },
                cancellationToken).ConfigureAwait(false);

            return new PurviewMappingGenerationOutcome(result.Document, persisted, Regenerated: true);
        }
        catch
        {
            // Rollback do STAGING (nunca de uma versão final): um staging não publicado é descartado. Uma
            // reserva pendente já commitada permanece para reconciliação idempotente numa nova tentativa.
            await _artifacts.DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    // FilePath/Name derivam EXATAMENTE do prefixo/nome remoto que o AzCopy realmente usou (item 5) —
    // nunca de WaveEntry.FilePath/PstName, que continuam sendo apenas planejamento.
    private static PurviewMappingRow BuildRow(TenantScope scope, WaveId waveId, TargetRootFolder targetRootFolder, PurviewMappingSourceRow row)
    {
        var prefix = PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, waveId);
        var name = PurviewRemotePstName.ForPart(row.Execution.Artifact, row.Execution.PartSequence).Value;
        return PurviewMappingRow.Create(prefix.WaveSegment, name, row.Entry.Archive.Mailbox, row.IsArchive, targetRootFolder);
    }
}
