namespace ArchiveBridge.Infrastructure.Mapping;

/// <summary>
/// Contenção robusta de caminhos de artefato dentro de uma raiz. Não usa <c>StartsWith</c> ingênuo (que
/// aceitaria um irmão de prefixo semelhante, ex.: raiz <c>/data/ab</c> vs. <c>/data/ab_evil</c>):
/// normaliza os caminhos e exige que o candidato seja a própria raiz ou esteja sob a raiz seguida do
/// separador. Além disso, rejeita pontos de reparse/symlinks na cadeia de diretórios existentes, que
/// poderiam escapar da raiz. Caminhos rooted/segmentos inesperados são cobertos pela normalização.
/// </summary>
public static class ArtifactPathContainment
{
    /// <summary>Garante que <paramref name="candidate"/> está contido em <paramref name="root"/>; lança se escapar.</summary>
    public static string EnsureContained(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Raiz de artefatos obrigatória.", nameof(root));
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("Caminho de artefato obrigatório.", nameof(candidate));
        }

        var rootFull = TrimSeparator(Path.GetFullPath(root));
        var candidateFull = TrimSeparator(Path.GetFullPath(candidate));

        var contained = string.Equals(candidateFull, rootFull, StringComparison.Ordinal)
            || candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!contained)
        {
            throw new ArgumentException($"Caminho de artefato fora da raiz configurada.", nameof(candidate));
        }

        RejectReparsePoints(rootFull, candidateFull);
        return candidateFull;
    }

    private static void RejectReparsePoints(string rootFull, string candidateFull)
    {
        // Percorre os diretórios existentes de rootFull (exclusive) até candidateFull: qualquer symlink/
        // reparse point na cadeia poderia redirecionar para fora da raiz — recusado (fail-closed).
        var current = candidateFull;
        while (!string.Equals(current, rootFull, StringComparison.Ordinal))
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ArgumentException("Caminho de artefato atravessa um symlink/reparse point.", nameof(candidateFull));
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }
    }

    private static string TrimSeparator(string path) =>
        path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
