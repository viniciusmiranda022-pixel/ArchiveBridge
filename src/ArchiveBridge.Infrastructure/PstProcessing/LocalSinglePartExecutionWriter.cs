using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Mapping;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Único <see cref="IPartitionPartWriter"/> deste Passo: materializa exclusivamente o caso já provado pelo
/// planner (<see cref="PartitionPlanReason.SinglePartWithinTarget"/>) como cópia byte-for-byte da origem —
/// nenhum split/rewrite/repair, nenhuma dependência de parser/vendor. Segue o MESMO protocolo recuperável de
/// duas fases já hardenado em <see cref="Mapping.FileSystemMappingArtifactStore"/> (stage fora do caminho
/// final ⇒ publica por <c>Directory.Move</c> atômico do bundle inteiro), adaptado para streaming (a origem
/// pode ter até <see cref="PartitionPolicy.RunbookHardPartBytes"/>, não cabe em memória):
/// <list type="number">
/// <item>Se o output final (caminho determinístico, derivado apenas de IDs opacos) já existe e confere com
/// o hash/tamanho esperados da origem, devolve-o sem reescrever — réplay/concorrência idempotentes.</item>
/// <item>Preflight de espaço em disco na raiz de output.</item>
/// <item>Copia a origem (sempre read-only, contenção/anti-symlink dupla, mesmo padrão de
/// <see cref="HeaderOnlyPstInspectionEngine"/>) para um arquivo de staging NOVO (nome aleatório por
/// tentativa — nunca reaproveita staging de uma tentativa anterior), hasheando em streaming.</item>
/// <item><b>Checkpoint 1:</b> flush/fsync do staged file, depois reabre-o e reconfere hash/tamanho —
/// nenhum output segue adiante sem essa confirmação.</item>
/// <item>Grava os sidecars (<c>part.sha256</c>, <c>manifest.json</c>) no MESMO diretório de staging.</item>
/// <item><b>Checkpoint 2:</b> publica o bundle inteiro por <c>Directory.Move</c> atômico para o caminho
/// final; se uma execução concorrente venceu a corrida (diretório final já existe), valida o bundle dela e
/// converge — nunca duplica, nunca sobrescreve.</item>
/// <item>Reabre e reinspeciona o bundle FINAL uma última vez antes de devolver sucesso ao chamador — só
/// depois disso a Application persiste o checkpoint 3 (o manifesto SQL, <see cref="IPartitionExecutionStore"/>).</item>
/// </list>
/// Um output final existente que NÃO confere é adulteração/corrupção: nunca sobrescrito automaticamente
/// (<see cref="PartitionExecutionOutputTamperedException"/>, fail-closed). Um diretório de staging órfão
/// (deixado por um crash real, sem exception handling algum) nunca é lido nem confundido com um output
/// concluído — a canonicidade é decidida SOMENTE pelo bundle validado no caminho final.
/// </summary>
public sealed class LocalSinglePartExecutionWriter : IPartitionPartWriter
{
    private const string PartFileName = "part.pst";
    private const string ShaFileName = "part.sha256";
    private const string ManifestFileName = "manifest.json";
    private const string StagingFolder = ".staging";

    // internal (não private): reaproveitado pelos testes de infraestrutura, mesmo padrão de
    // HeaderOnlyPstInspectionEngine.StreamBufferSize.
    internal const int StreamBufferSize = 4 * 1024 * 1024;

    private readonly PstStorageOptions _sourceOptions;
    private readonly PartitionExecutionOutputOptions _outputOptions;
    private readonly IPstArtifactStreamFactory _sourceStreamFactory;

    /// <summary>Constrói o writer com a factory física padrão de stream de origem (uso normal em produção/composition root).</summary>
    public LocalSinglePartExecutionWriter(PstStorageOptions sourceOptions, PartitionExecutionOutputOptions outputOptions)
        : this(sourceOptions, outputOptions, PhysicalPstArtifactStreamFactory.Instance)
    {
    }

    /// <summary>
    /// Construtor com seam de <see cref="IPstArtifactStreamFactory"/> injetável para a ORIGEM — visível
    /// apenas dentro do assembly (mais <c>InternalsVisibleTo</c> para <c>ArchiveBridge.Integration.Tests</c>),
    /// usado SOMENTE por testes que precisam simular falha/timeout/cancelamento no MEIO da cópia (crash-safety
    /// determinístico), exatamente como <see cref="HeaderOnlyPstInspectionEngine"/> já faz para AB-4B-003.
    /// Nunca usado pelo composition root — a sobrecarga pública acima sempre usa a factory física real.
    /// </summary>
    internal LocalSinglePartExecutionWriter(
        PstStorageOptions sourceOptions, PartitionExecutionOutputOptions outputOptions, IPstArtifactStreamFactory sourceStreamFactory)
    {
        _sourceOptions = sourceOptions;
        _outputOptions = outputOptions;
        _sourceStreamFactory = sourceStreamFactory;
    }

    /// <inheritdoc />
    public string ExecutorName => "ArchiveBridge.SinglePartByteCopyExecutor";

    /// <inheritdoc />
    public string ExecutorVersion => "1.0.0";

    /// <inheritdoc />
    public async Task<PartitionPartArtifact> ExecuteAsync(
        TenantScope scope, MigrationArtifact source, PartitionPlan plan, PartitionPlanPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(part);

        // Defesa em profundidade: mesmo que a Application já tenha validado a elegibilidade do plano, o
        // writer nunca confia cegamente no chamador — este é o ÚNICO shape que sabe materializar.
        if (!part.CoversEntireSource)
        {
            throw new PartitionExecutionNotEligibleException(
                "LocalSinglePartExecutionWriter só materializa partes que cobrem a origem inteira (SinglePartWithinTarget).");
        }

        var expectedSize = plan.Source.SourceSizeBytes
            ?? throw new PartitionExecutionNotEligibleException("Plano sem tamanho de origem observado.");
        var expectedHash = plan.Source.SourceHash;

        var finalDir = VersionDir(scope, plan, part);
        if (Directory.Exists(finalDir))
        {
            // Réplay idempotente/convergência de concorrência: o output canônico já existe neste caminho
            // determinístico. Reabre e reconfere ANTES de confiar — nunca aceita a mera existência do
            // diretório como prova de sucesso.
            return await ValidateBundleAsync(finalDir, expectedHash, expectedSize, cancellationToken).ConfigureAwait(false);
        }

        EnsurePreflightSpace(expectedSize);

        var sourcePath = ResolveSourcePath(source);

        var stagingRoot = ArtifactPathContainment.EnsureContained(_outputOptions.RootPath, Path.Combine(_outputOptions.RootPath, StagingFolder));
        Directory.CreateDirectory(stagingRoot);
        var stagingDir = ArtifactPathContainment.EnsureContained(stagingRoot, Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stagingDir);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_outputOptions.Timeout);

        try
        {
            var stagedPartPath = Path.Combine(stagingDir, PartFileName);
            var (writtenHash, writtenSize) = await CopySourceToStagingAsync(
                sourcePath, stagedPartPath, expectedSize, timeoutSource.Token).ConfigureAwait(false);

            if (writtenSize != expectedSize || writtenHash != expectedHash)
            {
                // A origem mudou (tamanho/conteúdo) entre o planejamento e esta cópia: fail-closed, nunca
                // publica um output que não corresponde ao que o plano autorizou.
                throw new PartitionExecutionSourceStaleException(
                    "A origem divergiu do esperado durante a materialização (tamanho/hash não confere com o plano).");
            }

            // Checkpoint 1 (crash-safety): reabre o arquivo de STAGING recém-escrito e reconfere de forma
            // totalmente independente do hash calculado durante a escrita — nenhum output segue adiante sem
            // esta confirmação.
            var (rehash, resize) = await HashFileAsync(stagedPartPath, timeoutSource.Token).ConfigureAwait(false);
            if (rehash != expectedHash || resize != expectedSize)
            {
                throw new PartitionExecutionOutputTamperedException(
                    "O arquivo de staging não confere após reabertura/reinspeção (fail-closed, nada publicado).");
            }

            await FlushSidecarsAsync(stagingDir, expectedHash, expectedSize, scope, plan, part, timeoutSource.Token).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
            try
            {
                // Checkpoint 2 (crash-safety): publicação atômica do bundle inteiro (part.pst + sha256 +
                // manifest.json) num único Directory.Move — nunca existe um estado "parcialmente publicado"
                // visível no caminho final.
                Directory.Move(stagingDir, finalDir);
            }
            catch (IOException) when (Directory.Exists(finalDir))
            {
                // Corrida: outra execução concorrente do MESMO plano/parte já publicou primeiro. Converge
                // para o resultado dela (após reconferir) em vez de duplicar/sobrescrever.
                var raced = await ValidateBundleAsync(finalDir, expectedHash, expectedSize, cancellationToken).ConfigureAwait(false);
                CleanupStagingBestEffort(stagingDir);
                return raced;
            }

            // Reabertura/reinspeção FINAL — só depois desta confirmação a Application publica o checkpoint 3
            // (o manifesto SQL). "PartCreated somente após sucesso" (runbook §20.4).
            return await ValidateBundleAsync(finalDir, expectedHash, expectedSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CleanupStagingBestEffort(stagingDir);
            throw new PartitionExecutionLimitExceededException("TIMEOUT");
        }
        catch
        {
            // Falha controlada (não um crash real): staging nunca fica pendurado à toa quando o código teve
            // chance de rodar. Um crash de verdade (sem exception handling algum) é tratado por reconciliação
            // fora deste caminho — ver TempStagingReconciler.
            CleanupStagingBestEffort(stagingDir);
            throw;
        }
    }

    private void EnsurePreflightSpace(long expectedSize)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_outputOptions.RootPath));
        if (string.IsNullOrEmpty(root))
        {
            return; // Sem raiz de volume resolvível (ex.: caminho relativo malformado) — deixa a escrita real revelar o erro.
        }

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!drive.IsReady)
        {
            return;
        }

        var required = expectedSize + _outputOptions.MinFreeSpaceMarginBytes;
        if (drive.AvailableFreeSpace < required)
        {
            throw new PartitionExecutionLimitExceededException("INSUFFICIENT_SPACE");
        }
    }

    private string ResolveSourcePath(MigrationArtifact source)
    {
        var candidate = Path.Combine(_sourceOptions.RootPath, source.RelativePath.Value);
        try
        {
            return ArtifactPathContainment.EnsureContained(_sourceOptions.RootPath, candidate);
        }
        catch (ArgumentException ex)
        {
            // Não deveria ocorrer para um relative_path validado no registro de custódia — tratado como
            // origem indisponível, nunca stack trace/caminho real na evidência.
            throw new PartitionExecutionSourceStaleException("Caminho de origem fora da raiz configurada (fail-closed).", ex);
        }
    }

    private async Task<(Sha256Hash Hash, long Size)> CopySourceToStagingAsync(
        string sourcePath, string stagedPartPath, long expectedSize, CancellationToken cancellationToken)
    {
        await using var source = _sourceStreamFactory.OpenRead(sourcePath);

        // Segunda checagem de contenção/reparse imediatamente após abrir, ANTES de ler qualquer byte —
        // estreita a janela TOCTOU exatamente como HeaderOnlyPstInspectionEngine faz.
        try
        {
            ArtifactPathContainment.EnsureContained(_sourceOptions.RootPath, sourcePath);
        }
        catch (ArgumentException ex)
        {
            throw new PartitionExecutionSourceStaleException("Origem atravessa symlink/reparse point (fail-closed).", ex);
        }

        await using var destination = new FileStream(
            stagedPartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, useAsync: true);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferSize];
        long total = 0;

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > expectedSize)
            {
                // A origem cresceu além do tamanho observado no plano: para de ler/escrever imediatamente —
                // nenhum byte além do esperado é jamais persistido no output.
                throw new PartitionExecutionSourceStaleException("A origem cresceu além do tamanho esperado durante a cópia.");
            }

            sha256.AppendData(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true); // fsync real antes de considerar os bytes duráveis.

        return (new Sha256Hash(Convert.ToHexStringLower(sha256.GetHashAndReset())), total);
    }

    private static async Task<(Sha256Hash Hash, long Size)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferSize];
        long total = 0;

        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            sha256.AppendData(buffer.AsSpan(0, read));
        }

        return (new Sha256Hash(Convert.ToHexStringLower(sha256.GetHashAndReset())), total);
    }

    // Validação do CONJUNTO completo de um bundle publicado (fonte única para réplay/convergência e para a
    // reabertura final antes de devolver sucesso). Exige os três arquivos (e SÓ eles), reconfere hash/
    // tamanho do part.pst e o sidecar sha256 contra o esperado (o hash/tamanho do PLANO, não o que o
    // manifesto AFIRMA — o manifesto é evidência, nunca a fonte de verdade da verificação).
    private static async Task<PartitionPartArtifact> ValidateBundleAsync(
        string finalDir, Sha256Hash expectedHash, long expectedSize, CancellationToken cancellationToken)
    {
        var partPath = Path.Combine(finalDir, PartFileName);
        var shaPath = Path.Combine(finalDir, ShaFileName);
        var manifestPath = Path.Combine(finalDir, ManifestFileName);
        if (!File.Exists(partPath) || !File.Exists(shaPath) || !File.Exists(manifestPath))
        {
            throw new PartitionExecutionOutputTamperedException(
                "Conjunto de output incompleto (arquivos ausentes); recusado (fail-closed).");
        }

        EnsureNoUnexpectedEntries(finalDir);

        var (hash, size) = await HashFileAsync(partPath, cancellationToken).ConfigureAwait(false);
        if (hash != expectedHash || size != expectedSize)
        {
            throw new PartitionExecutionOutputTamperedException(
                "Output existente no caminho canônico diverge do esperado; adulteração/corrupção detectada (fail-closed, nunca sobrescrito).");
        }

        var shaContent = (await File.ReadAllTextAsync(shaPath, cancellationToken).ConfigureAwait(false)).Trim();
        if (!string.Equals(shaContent, hash.Value, StringComparison.Ordinal))
        {
            throw new PartitionExecutionOutputTamperedException(
                "part.sha256 diverge do conteúdo de part.pst; artefato adulterado (fail-closed).");
        }

        return new PartitionPartArtifact(hash, size);
    }

    // O bundle publicado deve conter EXATAMENTE os três arquivos esperados — nenhum arquivo/subdiretório
    // inesperado (defesa contra material injetado no diretório da versão).
    private static void EnsureNoUnexpectedEntries(string finalDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(finalDir))
        {
            var name = Path.GetFileName(entry);
            var expected = string.Equals(name, PartFileName, StringComparison.Ordinal)
                || string.Equals(name, ShaFileName, StringComparison.Ordinal)
                || string.Equals(name, ManifestFileName, StringComparison.Ordinal);
            if (!expected || Directory.Exists(entry))
            {
                throw new PartitionExecutionOutputTamperedException(
                    "Bundle de output contém entrada inesperada; recusado (fail-closed).");
            }
        }
    }

    private static async Task FlushSidecarsAsync(
        string stagingDir, Sha256Hash hash, long size, TenantScope scope, PartitionPlan plan, PartitionPlanPart part,
        CancellationToken cancellationToken)
    {
        await FlushToDiskAsync(Path.Combine(stagingDir, ShaFileName), Encoding.UTF8.GetBytes(hash.Value + "\n"), cancellationToken)
            .ConfigureAwait(false);

        // Manifesto: APENAS IDs opacos, hashes e metadados de execução — nunca caminho, UPN, assunto, corpo,
        // nome de anexo ou segredo (work order AB-4B-006, item 7).
        var manifest = new PartitionExecutionManifestJson(
            scope.Tenant.Value,
            scope.Project.Value,
            plan.Id.Value,
            part.Id.Value,
            plan.PlanHash.Value,
            part.Sequence,
            part.PartKey.Value,
            hash.Value,
            size,
            "ArchiveBridge.SinglePartByteCopyExecutor",
            "1.0.0");
        await FlushToDiskAsync(Path.Combine(stagingDir, ManifestFileName), JsonSerializer.SerializeToUtf8Bytes(manifest), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task FlushToDiskAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void CleanupStagingBestEffort(string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Melhor esforço: um staging não removido aqui vira um órfão comum, tratado pela reconciliação
            // baseada em idade (ver TempStagingReconciler) — nunca mascara o erro original.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
    }

    private string VersionDir(TenantScope scope, PartitionPlan plan, PartitionPlanPart part)
    {
        var combined = Path.Combine(
            _outputOptions.RootPath,
            scope.Tenant.Value.ToString("N"),
            scope.Project.Value.ToString("N"),
            plan.Id.Value.ToString("N"),
            part.PartKey.Value);
        return ArtifactPathContainment.EnsureContained(_outputOptions.RootPath, combined);
    }

    private sealed record PartitionExecutionManifestJson(
        Guid Tenant,
        Guid Project,
        Guid Plan,
        Guid Part,
        string PlanHash,
        int PartSequence,
        string PartKey,
        string OutputHash,
        long OutputSizeBytes,
        string ExecutorName,
        string ExecutorVersion);
}
