namespace ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Canonicalização e limites do conjunto de estatísticas de pasta de UM snapshot (AB-I6-005 item 9):
/// bounded, deduplicado por identidade estável (<see cref="ExoArchiveFolderStatistic.FolderPath"/>) e
/// ordenado deterministicamente — a MESMA observação lógica produz o MESMO conjunto canônico
/// independentemente da ordem de entrada do adapter (critério de aceite 5). Função pura: nunca consulta
/// stores, nunca tem efeito colateral.
/// </summary>
public static class ExoArchiveFolderStatisticsSet
{
    /// <summary>Quantidade máxima de pastas por snapshot — excesso falha fechado (item 9).</summary>
    public const int MaxFolders = 2000;

    /// <summary>
    /// Valida limite/duplicidade e devolve as pastas ordenadas deterministicamente por
    /// <see cref="ExoArchiveFolderStatistic.FolderPath"/> (Ordinal) — nunca pela ordem de entrada do
    /// chamador/adapter.
    /// </summary>
    /// <exception cref="ExoArchiveStatisticsValidationException">Excesso de pastas ou <see cref="ExoArchiveFolderStatistic.FolderPath"/> duplicado.</exception>
    public static IReadOnlyList<ExoArchiveFolderStatistic> Canonicalize(IReadOnlyList<ExoArchiveFolderStatistic> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (folders.Count > MaxFolders)
        {
            throw new ExoArchiveStatisticsValidationException(
                $"O snapshot excede o limite de {MaxFolders} pastas (recebeu {folders.Count}) — fail-closed.");
        }

        var ordered = folders.OrderBy(folder => folder.FolderPath, StringComparer.Ordinal).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i].FolderPath, ordered[i - 1].FolderPath, StringComparison.Ordinal))
            {
                throw new ExoArchiveStatisticsValidationException(
                    $"Pasta duplicada no snapshot: '{ordered[i].FolderPath}' — identidade de pasta deve ser única (fail-closed).");
            }
        }

        return ordered;
    }
}
