using System.Globalization;
using System.Text;
using System.Text.Json;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;

namespace ArchiveBridge.Infrastructure.Mapping;

/// <summary>
/// Armazenamento IMUTÁVEL de artefatos de mapping em filesystem (baseline on-premises; SMB/NAS ao
/// apontar a raiz para o compartilhamento — sem dependência de Azure). Cada versão vai para um caminho
/// versionado <c>{tenant}/{project}/{wave}/v{n}</c> com <c>mapping.csv</c>, <c>mapping.sha256</c> e
/// <c>manifest.json</c>. A publicação é ATÔMICA e sem sobrescrita: escreve as evidências e o CSV em
/// arquivo temporário no mesmo diretório, faz flush ao disco e RENOMEIA o CSV para o nome final (o
/// ponto de publicação). Republicar a MESMA versão só é aceito se os bytes coincidem (idempotente);
/// bytes divergentes para a mesma versão são recusados (nunca sobrescreve a versão anterior). O CSV
/// contém apenas metadados de planejamento — sem segredo/PII.
/// </summary>
public sealed class FileSystemMappingArtifactStore(string rootDirectory) : IMappingArtifactStore
{
    private const string CsvFileName = "mapping.csv";
    private const string ShaFileName = "mapping.sha256";
    private const string ManifestFileName = "manifest.json";

    private readonly string _root = Path.GetFullPath(
        !string.IsNullOrWhiteSpace(rootDirectory)
            ? rootDirectory
            : throw new ArgumentException("A raiz de artefatos é obrigatória.", nameof(rootDirectory)));

    /// <inheritdoc />
    public async Task<MappingArtifactReference> SaveAsync(
        MappingArtifactDescriptor descriptor,
        ReadOnlyMemory<byte> csvBytes,
        Sha256Hash contentSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var logicalPath = LogicalPathFor(descriptor);
        var directory = ResolveDirectory(logicalPath);
        var csvFinal = Path.Combine(directory, CsvFileName);
        var bytes = csvBytes.ToArray();

        // Integridade: os bytes têm de corresponder ao SHA-256 informado (defesa em profundidade).
        var actual = DeterministicHash.ComputeBytes(bytes);
        if (!string.Equals(actual.Value, contentSha256.Value, StringComparison.Ordinal))
        {
            throw new MappingGenerationException("Bytes do artefato divergem do SHA-256 informado; recusado.");
        }

        if (File.Exists(csvFinal))
        {
            // Republicação idempotente da MESMA versão: só é aceita com bytes idênticos.
            var existing = await File.ReadAllBytesAsync(csvFinal, cancellationToken).ConfigureAwait(false);
            var existingHash = DeterministicHash.ComputeBytes(existing);
            if (!string.Equals(existingHash.Value, contentSha256.Value, StringComparison.Ordinal))
            {
                throw new MappingGenerationException(
                    "Já existe artefato divergente para esta versão de mapping; sobrescrita recusada (imutável).");
            }

            return new MappingArtifactReference(logicalPath, existing.LongLength, contentSha256);
        }

        Directory.CreateDirectory(directory);

        // Evidências primeiro (nomes finais); o CSV é publicado por último via rename atômico.
        await WriteAllAtomicAsync(Path.Combine(directory, ShaFileName), Encoding.UTF8.GetBytes(contentSha256.Value + "\n"), cancellationToken)
            .ConfigureAwait(false);
        await WriteAllAtomicAsync(Path.Combine(directory, ManifestFileName), BuildManifest(descriptor, contentSha256, bytes.LongLength, logicalPath), cancellationToken)
            .ConfigureAwait(false);

        var temporary = Path.Combine(directory, $"{CsvFileName}.{Guid.NewGuid():N}.tmp");
        await FlushToDiskAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, csvFinal);
        }
        catch (IOException) when (File.Exists(csvFinal))
        {
            // Corrida de publicação da mesma versão: aceita se os bytes coincidem, senão recusa.
            SafeDelete(temporary);
            var existing = await File.ReadAllBytesAsync(csvFinal, cancellationToken).ConfigureAwait(false);
            var existingHash = DeterministicHash.ComputeBytes(existing);
            if (!string.Equals(existingHash.Value, contentSha256.Value, StringComparison.Ordinal))
            {
                throw new MappingGenerationException(
                    "Publicação concorrente divergente para esta versão de mapping; recusada (imutável).");
            }
        }

        return new MappingArtifactReference(logicalPath, bytes.LongLength, contentSha256);
    }

    /// <inheritdoc />
    public async Task<MappingArtifactContent?> GetAsync(string logicalPath, CancellationToken cancellationToken)
    {
        var directory = ResolveDirectory(logicalPath);
        var csvFinal = Path.Combine(directory, CsvFileName);
        if (!File.Exists(csvFinal))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(csvFinal, cancellationToken).ConfigureAwait(false);
        return new MappingArtifactContent(bytes, DeterministicHash.ComputeBytes(bytes));
    }

    private static string LogicalPathFor(MappingArtifactDescriptor descriptor) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{descriptor.Scope.Tenant.Value:N}/{descriptor.Scope.Project.Value:N}/{descriptor.Wave.Value:N}/v{descriptor.Version.Value}");

    private string ResolveDirectory(string logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath) || logicalPath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Caminho lógico de artefato inválido.", nameof(logicalPath));
        }

        var segments = logicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var combined = Path.GetFullPath(Path.Combine([_root, .. segments]));
        if (!combined.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Caminho lógico de artefato fora da raiz.", nameof(logicalPath));
        }

        return combined;
    }

    private static byte[] BuildManifest(
        MappingArtifactDescriptor descriptor, Sha256Hash contentSha256, long sizeBytes, string logicalPath)
    {
        var manifest = new
        {
            tenant = descriptor.Scope.Tenant.Value,
            project = descriptor.Scope.Project.Value,
            wave = descriptor.Wave.Value,
            version = descriptor.Version.Value,
            logicalPath,
            contentSha256 = contentSha256.Value,
            sizeBytes,
        };
        return JsonSerializer.SerializeToUtf8Bytes(manifest);
    }

    private static async Task WriteAllAtomicAsync(string finalPath, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        await FlushToDiskAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, finalPath, overwrite: true);
        }
        catch
        {
            SafeDelete(temporary);
            throw;
        }
    }

    private static async Task FlushToDiskAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Melhor esforço na limpeza do temporário; não mascara o erro original.
        }
    }
}
