using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.Mapping;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Opções de configuração da raiz local de output de exportação (AB-4C-005 item 9): a raiz é SEMPRE
/// configuração server-side do connector host (nunca fornecida pelo cliente). Quando a raiz é um caminho
/// UNC (<c>\\server\share</c>), ela precisa constar explicitamente de <see cref="AllowedUncRoots"/> —
/// caso contrário a validação falha fechado no startup (§15.2: "rejeitar... UNC não allowlisted").
/// </summary>
public sealed record EvExportOutputRootOptions(string RootPath, IReadOnlyList<string> AllowedUncRoots)
{
    /// <summary>Valida a raiz configurada; lança fail-closed se for UNC e não allowlisted.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new ArgumentException("A raiz de output de exportação é obrigatória.", nameof(RootPath));
        }

        if (!IsUncPath(RootPath))
        {
            return;
        }

        var normalizedRoot = NormalizeUnc(RootPath);
        var allowed = AllowedUncRoots.Any(candidate => string.Equals(NormalizeUnc(candidate), normalizedRoot, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new EvExportOutputContainmentException(
                "A raiz de output de exportação é um caminho UNC não allowlisted (fail-closed, §15.2).");
        }
    }

    private static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string NormalizeUnc(string path) => path.TrimEnd('\\', '/').ToUpperInvariant();
}

/// <summary>
/// Resolve um <see cref="EvExportOutputLocation"/> para um caminho físico ABSOLUTO, sempre CONTIDO na raiz
/// autorizada do connector (AB-4C-005 item 9, §15.2): rejeita traversal, symlink/junction/reparse point na
/// cadeia de diretórios e qualquer escape de containment. Reutiliza o MESMO helper já hardenado
/// (<see cref="ArtifactPathContainment"/>) usado por <c>FileSystemEvDiscoveryEvidenceStore</c> e pela
/// execução de particionamento PST — nunca reimplementa a lógica de contenção.
/// </summary>
public static class EvExportOutputPaths
{
    /// <summary>Resolve o diretório físico de saída; fail-closed (<see cref="EvExportOutputContainmentException"/>) se escapar da raiz.</summary>
    public static string ResolvePhysicalDirectory(EvExportOutputLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        try
        {
            var root = Path.GetFullPath(location.ConnectorAuthorizedRoot);
            var candidate = Path.Combine(root, location.RelativeToken);
            return ArtifactPathContainment.EnsureContained(root, candidate);
        }
        catch (ArgumentException ex)
        {
            throw new EvExportOutputContainmentException(
                $"Diretório de saída de exportação fora da raiz autorizada do connector: {ex.Message}");
        }
    }
}
