using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Resultado da verificação de pré-requisitos (AB-I6-001 item 3): a evidência canônica RESOLVIDA agora, a
/// impressão digital fresca correspondente (idêntica à do mapping CSV publicado — sem drift) e o conjunto
/// canônico de nomes remotos de PST desta onda, pronto para correlação (item 8).
/// </summary>
internal sealed record PurviewImportJobEvidenceCheck(
    PurviewMappingEvidence Evidence,
    PurviewMappingGenerationFingerprint Fingerprint,
    IReadOnlyList<PurviewRemotePstName> CanonicalRemoteNames);

/// <summary>
/// Guarda de pré-requisitos COMPARTILHADA (AB-I6-001 item 3) por todos os casos de uso deste Passo que
/// precisam aceitar evidência de job/resultado do Purview: reaproveita
/// <see cref="ResolvePurviewMappingEvidenceUseCase"/> (a MESMA resolução server-side de vínculo/execução/
/// upload verificado/mailbox já aceita pelo Passo 4) e adiciona a verificação de que o mapping CSV
/// PUBLICADO/CANÔNICO da onda ainda corresponde EXATAMENTE a essa evidência — nenhum drift posterior de
/// binding/execution/upload/mapping é tolerado. Nunca automatiza portal/import job; apenas relê evidência
/// já persistida por Passos anteriores. Traduz as exceções do Passo 4 para o vocabulário deste Passo.
/// </summary>
internal static class PurviewImportJobEvidenceGuard
{
    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda inexistente/fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException">Upload/mapping não canônico, ou mapping publicado divergente da evidência atual (drift).</exception>
    public static async Task<PurviewImportJobEvidenceCheck> ResolveAndVerifyNoDriftAsync(
        ResolvePurviewMappingEvidenceUseCase evidenceResolver,
        IPurviewMappingCsvStore mappings,
        TenantScope scope,
        WaveId wave,
        CancellationToken cancellationToken)
    {
        PurviewMappingEvidence evidence;
        try
        {
            evidence = await evidenceResolver.ExecuteAsync(scope, wave, cancellationToken).ConfigureAwait(false);
        }
        catch (PurviewMappingCsvSourceNotFoundException exception)
        {
            throw new PurviewImportJobSourceNotFoundException(
                "Onda inexistente/fora do escopo autorizado (fail-closed).", exception);
        }
        catch (PurviewMappingCsvGenerationException exception)
        {
            throw new PurviewImportJobPrerequisiteException(
                "Pré-requisito de evidência canônica (upload/mapping/binding) não satisfeito: " + exception.Message, exception);
        }

        var usable = await mappings.GetUsableAsync(scope, wave, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobPrerequisiteException(
                "A onda não tem nenhum mapping CSV do Purview publicado/canônico ainda (fail-closed).");

        // Recomputa a impressão digital da EVIDÊNCIA ATUAL com o MESMO gerador puro do Passo 4 — nunca
        // persiste nada aqui (Generate é puro/sem I/O); usada SOMENTE para comparar com o fingerprint da
        // versão já publicada e detectar drift. O relógio/versão/autor são irrelevantes ao fingerprint
        // (PurviewMappingGenerationFingerprint.Compute nunca depende de tempo/versão/autor).
        var rows = evidence.Rows
            .Select(row => BuildMappingRow(scope, wave, evidence.Wave.TargetRootFolder, row))
            .ToList();
        var generation = PurviewMappingCsvGenerator.Generate(
            wave,
            evidence.Wave.Project,
            evidence.Wave.TargetRootFolder,
            rows,
            evidence.VerifiedAttempt.IdentityHash,
            MappingVersion.Initial,
            generatedBy: nameof(PurviewImportJobEvidenceGuard),
            now: DateTimeOffset.UnixEpoch);
        var freshFingerprint = generation.Evidence.Fingerprint;

        if (freshFingerprint != usable.Fingerprint)
        {
            throw new PurviewImportJobPrerequisiteException(
                "O mapping CSV publicado diverge da evidência canônica ATUAL (drift de vínculo/execução/upload/mailbox " +
                "desde a última publicação) — regenere o mapping antes de prosseguir (fail-closed).");
        }

        var canonicalRemoteNames = evidence.Rows
            .Select(row => PurviewRemotePstName.ForPart(row.Execution.Artifact, row.Execution.PartSequence))
            .ToList();

        return new PurviewImportJobEvidenceCheck(evidence, freshFingerprint, canonicalRemoteNames);
    }

    // FilePath/Name derivam EXATAMENTE do prefixo/nome remoto que o AzCopy realmente usou — mesma
    // derivação de GeneratePurviewMappingCsvUseCase.BuildRow (item 5: nunca WaveEntry.FilePath/PstName).
    private static PurviewMappingRow BuildMappingRow(
        TenantScope scope, WaveId waveId, TargetRootFolder targetRootFolder, PurviewMappingSourceRow row)
    {
        var prefix = PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, waveId);
        var name = PurviewRemotePstName.ForPart(row.Execution.Artifact, row.Execution.PartSequence).Value;
        return PurviewMappingRow.Create(prefix.WaveSegment, name, row.Entry.Archive.Mailbox, row.IsArchive, targetRootFolder);
    }
}
