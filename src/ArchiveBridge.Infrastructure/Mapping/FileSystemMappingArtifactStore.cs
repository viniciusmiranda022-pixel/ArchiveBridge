using System.Globalization;
using System.Text;
using System.Text.Json;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;

namespace ArchiveBridge.Infrastructure.Mapping;

/// <summary>
/// Armazenamento IMUTÁVEL de artefatos de mapping em filesystem (baseline on-premises; SMB/NAS ao
/// apontar a raiz — sem dependência de Azure). Protocolo recuperável em duas fases: os bytes são
/// encenados (<see cref="StageAsync"/>) FORA da transação SQL, e a publicação (<see cref="PublishAsync"/>)
/// RENOMEIA o diretório de staging inteiro para o destino versionado — os três arquivos
/// (<c>mapping.csv</c>, <c>mapping.sha256</c>, <c>manifest.json</c>) aparecem como uma ÚNICA unidade
/// atômica. Nenhum arquivo final é sobrescrito; republicação idêntica é idempotente e divergente é
/// recusada. A leitura valida o conjunto (hash, sha256, manifesto vs. escopo) e falha fechada.
/// <b>Atenção:</b> o CSV contém dados pessoais/metadados de planejamento (Mailbox, FilePath, nome de
/// PST) — a raiz deve ter ACL restritiva equivalente ao SQL sob RLS.
/// </summary>
public sealed class FileSystemMappingArtifactStore : IMappingArtifactStore
{
    private const string CsvFileName = "mapping.csv";
    private const string ShaFileName = "mapping.sha256";
    private const string ManifestFileName = "manifest.json";
    private const string StagingFolder = ".staging";

    private readonly string _root;

    /// <summary>Cria o armazenamento com a raiz configurada (deve existir sob ACL restritiva).</summary>
    public FileSystemMappingArtifactStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A raiz de artefatos é obrigatória.", nameof(rootDirectory));
        }

        _root = Path.GetFullPath(rootDirectory);
    }

    /// <inheritdoc />
    public async Task<MappingArtifactStaging> StageAsync(
        ReadOnlyMemory<byte> csvBytes, Sha256Hash contentSha256, CancellationToken cancellationToken)
    {
        var bytes = csvBytes.ToArray();
        var actual = DeterministicHash.ComputeBytes(bytes);
        if (!string.Equals(actual.Value, contentSha256.Value, StringComparison.Ordinal))
        {
            throw new MappingGenerationException("Bytes do artefato divergem do SHA-256 informado; encenamento recusado.");
        }

        var stagingRoot = ArtifactPathContainment.EnsureContained(_root, Path.Combine(_root, StagingFolder));
        var stagingDir = ArtifactPathContainment.EnsureContained(stagingRoot, Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stagingDir);

        await FlushToDiskAsync(Path.Combine(stagingDir, CsvFileName), bytes, cancellationToken).ConfigureAwait(false);
        await FlushToDiskAsync(Path.Combine(stagingDir, ShaFileName), Encoding.UTF8.GetBytes(contentSha256.Value + "\n"), cancellationToken)
            .ConfigureAwait(false);

        return new MappingArtifactStaging(stagingDir, bytes.LongLength, contentSha256);
    }

    /// <inheritdoc />
    public async Task<MappingArtifactReference> PublishAsync(
        MappingArtifactStaging staging, MappingArtifactDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(descriptor);
        var logicalPath = descriptor.LogicalPath;
        var finalDir = VersionDir(descriptor);
        var reference = new MappingArtifactReference(logicalPath, staging.SizeBytes, staging.ContentSha256);

        if (Directory.Exists(finalDir))
        {
            // Já publicado: republicação idempotente EXIGE que o bundle COMPLETO (csv + sha256 + manifesto,
            // hashes, campos do manifesto vs. escopo/versão, ausência de arquivos inesperados, contenção)
            // corresponda ao descritor e ao conteúdo encenado. Divergência ou bundle incompleto ⇒ recusa
            // (imutável, fail-closed) — não descarta o staging válido nem sobrescreve.
            await EnsureExistingBundleMatchesAsync(descriptor, staging.ContentSha256, cancellationToken).ConfigureAwait(false);
            await DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
            return reference;
        }

        // Escreve o manifesto (com versão) no staging e publica o CONJUNTO por rename atômico do diretório.
        await FlushToDiskAsync(
            Path.Combine(staging.StagingPath, ManifestFileName),
            BuildManifest(descriptor, staging.ContentSha256, staging.SizeBytes, logicalPath),
            cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
        try
        {
            Directory.Move(staging.StagingPath, finalDir);
        }
        catch (IOException) when (Directory.Exists(finalDir))
        {
            // Corrida de publicação da mesma versão: valida o bundle COMPLETO e recusa divergência. Descarta o staging.
            await EnsureExistingBundleMatchesAsync(descriptor, staging.ContentSha256, cancellationToken).ConfigureAwait(false);
            await DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
        }

        return reference;
    }

    /// <inheritdoc />
    public async Task<MappingArtifactContent?> GetAsync(MappingArtifactDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var finalDir = VersionDir(descriptor);
        if (!Directory.Exists(finalDir))
        {
            return null;
        }

        return await ValidateBundleAsync(finalDir, descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DiscardAsync(MappingArtifactStaging staging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);

        // Guarda de destruição: SÓ se descarta um diretório ESTRITAMENTE sob <root>/.staging. Um caminho
        // arbitrário, a própria raiz de staging ou um symlink/reparse são recusados ANTES de qualquer
        // Directory.Delete — nunca se apaga fora da área de encenação autorizada (fail-closed).
        var stagingRoot = ArtifactPathContainment.EnsureContained(_root, Path.Combine(_root, StagingFolder));
        var target = ArtifactPathContainment.EnsureContained(stagingRoot, staging.StagingPath);
        if (string.Equals(target, stagingRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("Descarte recusado: o caminho é a própria raiz de staging, não um staging individual.", nameof(staging));
        }

        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: não mascara o erro original de uma falha de publicação.
        }

        return Task.CompletedTask;
    }

    // Validação do CONJUNTO completo de um bundle publicado (fonte única para leitura e para a
    // revalidação de republicação). Exige os três arquivos (e SÓ eles), o hash do CSV, o conteúdo do
    // sidecar sha256 e o manifesto vs. tenant/projeto/onda/versão/caminho/tamanho/ContentSha256.
    private static async Task<MappingArtifactContent> ValidateBundleAsync(
        string finalDir, MappingArtifactDescriptor descriptor, CancellationToken cancellationToken)
    {
        var csvPath = Path.Combine(finalDir, CsvFileName);
        var shaPath = Path.Combine(finalDir, ShaFileName);
        var manifestPath = Path.Combine(finalDir, ManifestFileName);
        if (!File.Exists(csvPath) || !File.Exists(shaPath) || !File.Exists(manifestPath))
        {
            throw new MappingGenerationException("Conjunto de artefato incompleto (arquivos ausentes); recusado (fail-closed).");
        }

        EnsureNoUnexpectedEntries(finalDir);

        var bytes = await File.ReadAllBytesAsync(csvPath, cancellationToken).ConfigureAwait(false);
        var actualHash = DeterministicHash.ComputeBytes(bytes);

        var shaContent = (await File.ReadAllTextAsync(shaPath, cancellationToken).ConfigureAwait(false)).Trim();
        if (!string.Equals(shaContent, actualHash.Value, StringComparison.Ordinal))
        {
            throw new MappingGenerationException("mapping.sha256 diverge do conteúdo do CSV; artefato adulterado (fail-closed).");
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<ArtifactManifest>(manifestJson)
            ?? throw new MappingGenerationException("Manifesto de artefato ilegível (fail-closed).");
        ValidateManifest(manifest, descriptor, actualHash, bytes.LongLength);

        return new MappingArtifactContent(bytes, actualHash);
    }

    // O bundle publicado deve conter EXATAMENTE os três arquivos esperados — nenhum arquivo/subdiretório
    // inesperado (defesa contra material injetado no diretório da versão).
    private static void EnsureNoUnexpectedEntries(string finalDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(finalDir))
        {
            var name = Path.GetFileName(entry);
            var expected = string.Equals(name, CsvFileName, StringComparison.Ordinal)
                || string.Equals(name, ShaFileName, StringComparison.Ordinal)
                || string.Equals(name, ManifestFileName, StringComparison.Ordinal);
            if (!expected || Directory.Exists(entry))
            {
                throw new MappingGenerationException("Bundle contém entrada inesperada na pasta da versão; recusado (fail-closed).");
            }
        }
    }

    private static void ValidateManifest(
        ArtifactManifest manifest, MappingArtifactDescriptor descriptor, Sha256Hash actualHash, long sizeBytes)
    {
        var ok = manifest.Tenant == descriptor.Scope.Tenant.Value
            && manifest.Project == descriptor.Scope.Project.Value
            && manifest.Wave == descriptor.Wave.Value
            && manifest.Version == descriptor.Version.Value
            && string.Equals(manifest.LogicalPath, descriptor.LogicalPath, StringComparison.Ordinal)
            && string.Equals(manifest.ContentSha256, actualHash.Value, StringComparison.Ordinal)
            && manifest.SizeBytes == sizeBytes;
        if (!ok)
        {
            throw new MappingGenerationException("Manifesto do artefato não corresponde ao escopo/conteúdo esperado (fail-closed).");
        }
    }

    // Revalidação de republicação: valida o bundle COMPLETO já publicado (não só o hash do CSV) contra o
    // descritor e confere que o conteúdo é IDÊNTICO ao encenado. Bundle incompleto/divergente ⇒ recusa
    // (imutável, fail-closed) — não sobrescreve nem descarta o staging válido.
    private async Task EnsureExistingBundleMatchesAsync(
        MappingArtifactDescriptor descriptor, Sha256Hash expected, CancellationToken cancellationToken)
    {
        var finalDir = VersionDir(descriptor);
        var content = await ValidateBundleAsync(finalDir, descriptor, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(content.ContentSha256.Value, expected.Value, StringComparison.Ordinal))
        {
            throw new MappingGenerationException(
                "Já existe artefato divergente para esta versão de mapping; sobrescrita recusada (imutável).");
        }
    }

    private string VersionDir(MappingArtifactDescriptor descriptor)
    {
        var combined = Path.Combine(
            _root,
            descriptor.Scope.Tenant.Value.ToString("N"),
            descriptor.Scope.Project.Value.ToString("N"),
            descriptor.Wave.Value.ToString("N"),
            string.Create(CultureInfo.InvariantCulture, $"v{descriptor.Version.Value}"));
        return ArtifactPathContainment.EnsureContained(_root, combined);
    }

    private static byte[] BuildManifest(
        MappingArtifactDescriptor descriptor, Sha256Hash contentSha256, long sizeBytes, string logicalPath)
    {
        var manifest = new ArtifactManifest(
            descriptor.Scope.Tenant.Value,
            descriptor.Scope.Project.Value,
            descriptor.Wave.Value,
            descriptor.Version.Value,
            logicalPath,
            contentSha256.Value,
            sizeBytes);
        return JsonSerializer.SerializeToUtf8Bytes(manifest);
    }

    private static async Task FlushToDiskAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private sealed record ArtifactManifest(
        Guid Tenant, Guid Project, Guid Wave, int Version, string LogicalPath, string ContentSha256, long SizeBytes);
}
