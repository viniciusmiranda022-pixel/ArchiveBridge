using System.Security.Cryptography;
using System.Text.Json;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Validação do CONJUNTO completo de um bundle de output publicado (<c>part.pst</c> + <c>part.sha256</c> +
/// <c>manifest.json</c>) — fonte ÚNICA compartilhada por <see cref="LocalSinglePartExecutionWriter"/> (réplay
/// interno durante a materialização, convergência de concorrência, reabertura final antes de devolver
/// sucesso) e por <see cref="LocalSinglePartExecutionVerifier"/> (revalidação de um checkpoint JÁ PERSISTIDO
/// antes de reaproveitá-lo em réplay pela Application — correção AB-4B-007). SOMENTE LEITURA: nunca cria,
/// sobrescreve ou repara nada — qualquer divergência é <see cref="PartitionExecutionOutputTamperedException"/>
/// fail-closed.
/// <para>
/// Reconfere hash/tamanho do <c>part.pst</c> e o sidecar <c>part.sha256</c> contra o esperado (o hash/tamanho
/// do PLANO/registro persistido, nunca o que o manifesto AFIRMA), e valida o CONTEÚDO estrutural do
/// <c>manifest.json</c> contra os mesmos valores esperados — um manifesto adulterado nunca passa só porque
/// <c>part.pst</c> e <c>.sha256</c> permanecem válidos.
/// </para>
/// </summary>
internal static class PartitionOutputBundleValidator
{
    internal const string PartFileName = "part.pst";
    internal const string ShaFileName = "part.sha256";
    internal const string ManifestFileName = "manifest.json";

    // internal (não private): reaproveitado pelo writer para o buffer de streaming da cópia/hash.
    internal const int StreamBufferSize = 4 * 1024 * 1024;

    /// <summary>Conteúdo estrutural esperado do manifesto, derivado do registro persistido/plano/parte (nunca do próprio manifesto).</summary>
    internal readonly record struct ExpectedManifest(
        Guid Tenant,
        Guid Project,
        Guid Plan,
        Guid Part,
        Sha256Hash PlanHash,
        int PartSequence,
        Sha256Hash PartKey,
        string ExecutorName,
        string ExecutorVersion);

    /// <summary>Caminho determinístico do diretório de output de uma parte, derivado apenas de IDs opacos.</summary>
    internal static string VersionDir(string outputRoot, Guid tenant, Guid project, Guid plan, string partKey)
    {
        var combined = Path.Combine(outputRoot, tenant.ToString("N"), project.ToString("N"), plan.ToString("N"), partKey);
        return Mapping.ArtifactPathContainment.EnsureContained(outputRoot, combined);
    }

    /// <summary>
    /// Exige os três arquivos do bundle (e SÓ eles), reconfere hash/tamanho de <c>part.pst</c>, o sidecar
    /// <c>part.sha256</c> e o conteúdo estrutural de <c>manifest.json</c> contra o esperado. Nunca escreve
    /// nada no disco.
    /// </summary>
    /// <exception cref="PartitionExecutionOutputTamperedException">Qualquer divergência (ou ausência) do bundle.</exception>
    internal static async Task<PartitionPartArtifact> ValidateAsync(
        string finalDir, Sha256Hash expectedHash, long expectedSize, ExpectedManifest expectedManifest, CancellationToken cancellationToken)
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

        await ValidateManifestAsync(manifestPath, hash, size, expectedManifest, cancellationToken).ConfigureAwait(false);

        return new PartitionPartArtifact(hash, size);
    }

    // O manifesto é evidência, NUNCA a fonte de verdade da verificação: todo campo estrutural (IDs opacos,
    // PlanHash, PartSequence/PartKey, hash/size, identidade do executor) tem de bater EXATAMENTE com o
    // esperado — um manifesto que "concorda consigo mesmo" mas diverge do registro persistido é adulteração.
    private static async Task ValidateManifestAsync(
        string manifestPath, Sha256Hash hash, long size, ExpectedManifest expected, CancellationToken cancellationToken)
    {
        PartitionExecutionManifestJson? manifest;
        try
        {
            var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<PartitionExecutionManifestJson>(bytes);
        }
        catch (JsonException ex)
        {
            throw new PartitionExecutionOutputTamperedException(
                "manifest.json malformado; artefato adulterado (fail-closed).", ex);
        }

        var isConsistent = manifest is not null
            && manifest.Tenant == expected.Tenant
            && manifest.Project == expected.Project
            && manifest.Plan == expected.Plan
            && manifest.Part == expected.Part
            && string.Equals(manifest.PlanHash, expected.PlanHash.Value, StringComparison.Ordinal)
            && manifest.PartSequence == expected.PartSequence
            && string.Equals(manifest.PartKey, expected.PartKey.Value, StringComparison.Ordinal)
            && string.Equals(manifest.OutputHash, hash.Value, StringComparison.Ordinal)
            && manifest.OutputSizeBytes == size
            && string.Equals(manifest.ExecutorName, expected.ExecutorName, StringComparison.Ordinal)
            && string.Equals(manifest.ExecutorVersion, expected.ExecutorVersion, StringComparison.Ordinal);

        if (!isConsistent)
        {
            throw new PartitionExecutionOutputTamperedException(
                "manifest.json diverge do registro de execução esperado; artefato adulterado (fail-closed).");
        }
    }

    internal static async Task<(Sha256Hash Hash, long Size)> HashFileAsync(string path, CancellationToken cancellationToken)
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

    /// <summary>
    /// Forma serializada do manifesto (SÓ IDs opacos, hashes e metadados de execução — nunca caminho, UPN,
    /// assunto, corpo, nome de anexo ou segredo). Compartilhada entre a escrita (<see cref="LocalSinglePartExecutionWriter"/>)
    /// e a leitura/validação (aqui).
    /// </summary>
    internal sealed record PartitionExecutionManifestJson(
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
