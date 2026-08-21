using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Único <see cref="IPartitionPartVerifier"/> deste Passo (correção AB-4B-007): revalida, SOMENTE LEITURA, o
/// bundle publicado no MESMO caminho canônico determinístico que <see cref="LocalSinglePartExecutionWriter"/>
/// usa para materializar (<see cref="PartitionOutputBundleValidator.VersionDir"/>) — nunca cria, sobrescreve
/// ou reconstrói nada. Compartilha o MESMO validador de bundle usado pelo writer
/// (<see cref="PartitionOutputBundleValidator"/>), garantindo que réplay de um checkpoint já persistido e a
/// escrita/reabertura originais apliquem EXATAMENTE as mesmas checagens (hash/tamanho de <c>part.pst</c>,
/// sidecar <c>part.sha256</c> e conteúdo estrutural de <c>manifest.json</c>). Output ausente no caminho
/// canônico é tratado como adulteração — nunca como "recriar".
/// </summary>
public sealed class LocalSinglePartExecutionVerifier(PartitionExecutionOutputOptions outputOptions) : IPartitionPartVerifier
{
    private readonly PartitionExecutionOutputOptions _outputOptions = outputOptions;

    /// <inheritdoc />
    public async Task VerifyAsync(TenantScope scope, PartitionExecutionRecord execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var finalDir = PartitionOutputBundleValidator.VersionDir(
            _outputOptions.RootPath, scope.Tenant.Value, scope.Project.Value, execution.Plan.Value, execution.PartKey.Value);

        if (!Directory.Exists(finalDir))
        {
            // O checkpoint SQL existe, mas o bundle físico correspondente sumiu (removido, disco trocado,
            // etc.): fail-closed — nunca reconstruído silenciosamente a partir de um "replay barato".
            throw new PartitionExecutionOutputTamperedException(
                "Output do checkpoint persistido não existe mais no caminho canônico (fail-closed; nunca reconstruído).");
        }

        // Réplay de um checkpoint JÁ PERSISTIDO (AB-4B-007): diferente da convergência interna do writer, AQUI
        // sempre existe um valor esperado independente para source_hash/correlation_id/started_at_utc/
        // completed_at_utc — o registro SQL persistido — então os quatro são checados por igualdade estrita
        // contra o manifesto físico (correção AB-4B-008, work order AB-4B-006 item 7). Um manifesto que só
        // "concorda consigo mesmo" nunca passa aqui.
        var expectedManifest = new PartitionOutputBundleValidator.ExpectedManifest(
            scope.Tenant.Value,
            scope.Project.Value,
            execution.Plan.Value,
            execution.Part.Value,
            execution.PlanHash,
            execution.PartSequence,
            execution.PartKey,
            execution.SourceHash,
            execution.Executor.Name,
            execution.Executor.Version,
            execution.Correlation,
            execution.StartedAtUtc,
            execution.CompletedAtUtc);

        // ValidateAsync é puramente de LEITURA: só compara o que encontra no disco contra o esperado — nunca
        // escreve, sobrescreve ou apaga nada, mesmo quando encontra divergência.
        await PartitionOutputBundleValidator.ValidateAsync(
            finalDir, execution.OutputHash, execution.OutputSizeBytes, expectedManifest, cancellationToken).ConfigureAwait(false);
    }
}
