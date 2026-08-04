using System.Globalization;
using System.Text;
using System.Text.Json;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.Mapping;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Armazenamento IMUTÁVEL da evidência de descoberta em filesystem (baseline on-premises; SMB/NAS ao
/// apontar a raiz). Mesmo protocolo recuperável em duas fases do Slice 2: <see cref="StageAsync"/> escreve
/// <c>evidence.json</c> + <c>evidence.sha256</c> num diretório de staging FORA de qualquer transação SQL;
/// <see cref="PublishAsync"/> escreve o manifesto e RENOMEIA o diretório inteiro para o destino versionado
/// (os três arquivos aparecem como uma ÚNICA unidade atômica). A leitura valida o CONJUNTO completo (os
/// três arquivos e só eles, hash, sha256, manifesto vs. escopo/versão/tamanho/caminho) e falha fechada.
/// <b>Atenção:</b> a evidência pode conter nomes técnicos de servidores/componentes — informação sensível
/// de infraestrutura; a raiz deve ter ACL restritiva.
/// </summary>
public sealed class FileSystemEvDiscoveryEvidenceStore : IEvDiscoveryEvidenceStore
{
    private const string EvidenceFileName = "evidence.json";
    private const string ShaFileName = "evidence.sha256";
    private const string ManifestFileName = "manifest.json";
    private const string StagingFolder = ".staging";

    private readonly string _root;

    /// <summary>Cria o armazenamento com a raiz configurada (deve existir sob ACL restritiva).</summary>
    public FileSystemEvDiscoveryEvidenceStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A raiz de evidência é obrigatória.", nameof(rootDirectory));
        }

        _root = Path.GetFullPath(rootDirectory);
    }

    /// <inheritdoc />
    public async Task<EvDiscoveryEvidenceStaging> StageAsync(
        ReadOnlyMemory<byte> evidenceBytes, Sha256Hash contentSha256, CancellationToken cancellationToken)
    {
        var bytes = evidenceBytes.ToArray();
        var actual = DeterministicHash.ComputeBytes(bytes);
        if (!string.Equals(actual.Value, contentSha256.Value, StringComparison.Ordinal))
        {
            throw new EvDiscoveryEvidenceException("Bytes da evidência divergem do SHA-256 informado; encenamento recusado.");
        }

        var stagingRoot = ArtifactPathContainment.EnsureContained(_root, Path.Combine(_root, StagingFolder));
        var stagingDir = ArtifactPathContainment.EnsureContained(stagingRoot, Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stagingDir);

        await FlushToDiskAsync(Path.Combine(stagingDir, EvidenceFileName), bytes, cancellationToken).ConfigureAwait(false);
        await FlushToDiskAsync(Path.Combine(stagingDir, ShaFileName), Encoding.UTF8.GetBytes(contentSha256.Value + "\n"), cancellationToken)
            .ConfigureAwait(false);

        return new EvDiscoveryEvidenceStaging(stagingDir, bytes.LongLength, contentSha256);
    }

    /// <inheritdoc />
    public async Task<EvDiscoveryEvidenceReference> PublishAsync(
        EvDiscoveryEvidenceStaging staging, EvDiscoveryEvidenceDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(descriptor);
        var logicalPath = descriptor.LogicalPath;
        var finalDir = VersionDir(descriptor);
        var reference = new EvDiscoveryEvidenceReference(logicalPath, staging.SizeBytes, staging.ContentSha256);

        if (Directory.Exists(finalDir))
        {
            // Republicação idempotente: valida o BUNDLE COMPLETO vs. descritor + conteúdo; divergência recusada.
            await EnsureExistingBundleMatchesAsync(descriptor, staging.ContentSha256, cancellationToken).ConfigureAwait(false);
            await DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
            return reference;
        }

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
            await EnsureExistingBundleMatchesAsync(descriptor, staging.ContentSha256, cancellationToken).ConfigureAwait(false);
            await DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
        }

        return reference;
    }

    /// <inheritdoc />
    public async Task<EvDiscoveryEvidenceContent?> GetAsync(EvDiscoveryEvidenceDescriptor descriptor, CancellationToken cancellationToken)
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
    public Task DiscardAsync(EvDiscoveryEvidenceStaging staging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);

        // Guarda de destruição: SÓ diretório ESTRITAMENTE sob <root>/.staging; recusa arbitrário/reparse
        // ANTES de qualquer Directory.Delete (fail-closed).
        var stagingRoot = ArtifactPathContainment.EnsureContained(_root, Path.Combine(_root, StagingFolder));
        var target = ArtifactPathContainment.EnsureContained(stagingRoot, staging.StagingPath);
        if (string.Equals(target, stagingRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("Descarte recusado: caminho é a própria raiz de staging.", nameof(staging));
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

    private static async Task<EvDiscoveryEvidenceContent> ValidateBundleAsync(
        string finalDir, EvDiscoveryEvidenceDescriptor descriptor, CancellationToken cancellationToken)
    {
        var evidencePath = Path.Combine(finalDir, EvidenceFileName);
        var shaPath = Path.Combine(finalDir, ShaFileName);
        var manifestPath = Path.Combine(finalDir, ManifestFileName);
        if (!File.Exists(evidencePath) || !File.Exists(shaPath) || !File.Exists(manifestPath))
        {
            throw new EvDiscoveryEvidenceException("Conjunto de evidência incompleto (arquivos ausentes); recusado (fail-closed).");
        }

        EnsureNoUnexpectedEntries(finalDir);

        var bytes = await File.ReadAllBytesAsync(evidencePath, cancellationToken).ConfigureAwait(false);
        var actualHash = DeterministicHash.ComputeBytes(bytes);

        var shaContent = (await File.ReadAllTextAsync(shaPath, cancellationToken).ConfigureAwait(false)).Trim();
        if (!string.Equals(shaContent, actualHash.Value, StringComparison.Ordinal))
        {
            throw new EvDiscoveryEvidenceException("evidence.sha256 diverge do conteúdo; evidência adulterada (fail-closed).");
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<EvidenceManifest>(manifestJson)
            ?? throw new EvDiscoveryEvidenceException("Manifesto de evidência ilegível (fail-closed).");
        ValidateManifest(manifest, descriptor, actualHash, bytes.LongLength);

        return new EvDiscoveryEvidenceContent(bytes, actualHash);
    }

    private static void EnsureNoUnexpectedEntries(string finalDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(finalDir))
        {
            var name = Path.GetFileName(entry);
            var expected = string.Equals(name, EvidenceFileName, StringComparison.Ordinal)
                || string.Equals(name, ShaFileName, StringComparison.Ordinal)
                || string.Equals(name, ManifestFileName, StringComparison.Ordinal);
            if (!expected || Directory.Exists(entry))
            {
                throw new EvDiscoveryEvidenceException("Bundle de evidência contém entrada inesperada; recusado (fail-closed).");
            }
        }
    }

    private static void ValidateManifest(
        EvidenceManifest manifest, EvDiscoveryEvidenceDescriptor descriptor, Sha256Hash actualHash, long sizeBytes)
    {
        var ok = manifest.Tenant == descriptor.Scope.Tenant.Value
            && manifest.Project == descriptor.Scope.Project.Value
            && manifest.Environment == descriptor.Environment.Value
            && manifest.Version == descriptor.DiscoveryVersion
            && string.Equals(manifest.LogicalPath, descriptor.LogicalPath, StringComparison.Ordinal)
            && string.Equals(manifest.ContentSha256, actualHash.Value, StringComparison.Ordinal)
            && manifest.SizeBytes == sizeBytes;
        if (!ok)
        {
            throw new EvDiscoveryEvidenceException("Manifesto da evidência não corresponde ao escopo/conteúdo (fail-closed).");
        }
    }

    private async Task EnsureExistingBundleMatchesAsync(
        EvDiscoveryEvidenceDescriptor descriptor, Sha256Hash expected, CancellationToken cancellationToken)
    {
        var finalDir = VersionDir(descriptor);
        var content = await ValidateBundleAsync(finalDir, descriptor, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(content.ContentSha256.Value, expected.Value, StringComparison.Ordinal))
        {
            throw new EvDiscoveryEvidenceException(
                "Já existe evidência divergente para esta versão; sobrescrita recusada (imutável).");
        }
    }

    private string VersionDir(EvDiscoveryEvidenceDescriptor descriptor)
    {
        var combined = Path.Combine(
            _root,
            descriptor.Scope.Tenant.Value.ToString("N"),
            descriptor.Scope.Project.Value.ToString("N"),
            descriptor.Environment.Value.ToString("N"),
            string.Create(CultureInfo.InvariantCulture, $"v{descriptor.DiscoveryVersion}"));
        return ArtifactPathContainment.EnsureContained(_root, combined);
    }

    private static byte[] BuildManifest(
        EvDiscoveryEvidenceDescriptor descriptor, Sha256Hash contentSha256, long sizeBytes, string logicalPath)
    {
        var manifest = new EvidenceManifest(
            descriptor.Scope.Tenant.Value,
            descriptor.Scope.Project.Value,
            descriptor.Environment.Value,
            descriptor.DiscoveryVersion,
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

    private sealed record EvidenceManifest(
        Guid Tenant, Guid Project, Guid Environment, int Version, string LogicalPath, string ContentSha256, long SizeBytes);
}
