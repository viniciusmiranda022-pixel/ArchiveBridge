using System.Security.Cryptography;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Infrastructure.Mapping;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Varredura de outputs PST (AB-4C-005 items 9/10): resolve o diretório de saída SOB a raiz autorizada do
/// connector (fail-closed em traversal/reparse), enumera <c>*.pst</c> de PRIMEIRO NÍVEL (nenhuma
/// recursão — o exporter nunca cria subdiretórios de output) e calcula tamanho + SHA-256 EM STREAMING após
/// o fechamento de cada arquivo. O nome de arquivo é preservado apenas como <c>FileNameHint</c> — a
/// identidade canônica é sempre derivada do hash pelo Domain (nunca confia no nome como identidade).
/// </summary>
public sealed class FileSystemEvExportOutputInspector : IEvExportOutputInspector
{
    private const int StreamBufferSize = 1024 * 1024;

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvExportOutputCandidate>> ScanPstOutputsAsync(
        EvExportOutputLocation location, CancellationToken cancellationToken)
    {
        var directory = EvExportOutputPaths.ResolvePhysicalDirectory(location);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var candidates = new List<EvExportOutputCandidate>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.pst", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Contenção/anti-reparse por arquivo — estreita a janela TOCTOU (mesmo padrão de
            // HeaderOnlyPstInspectionEngine/LocalSinglePartExecutionWriter).
            var contained = ArtifactPathContainment.EnsureContained(directory, file);
            var (hash, size) = await HashFileAsync(contained, cancellationToken).ConfigureAwait(false);
            candidates.Add(new EvExportOutputCandidate(Path.GetFileName(contained), size, hash));
        }

        return candidates;
    }

    private static async Task<(Sha256Hash Hash, long Size)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, useAsync: true);
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
}
