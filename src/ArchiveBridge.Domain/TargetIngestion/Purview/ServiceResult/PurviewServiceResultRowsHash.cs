using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Hash determinístico do conjunto COMPLETO de linhas normalizadas de uma versão de service result
/// report (AB-I6-001 item 9) — participa da evidência persistida e é revalidado a cada leitura (mesmo
/// princípio de <see cref="Upload.PurviewUploadFileManifestHash"/>/<c>binding_hash</c>): adulterar
/// qualquer linha (inclusive inserir/remover/duplicar/alterar um contador) é detectado fail-closed.
/// </summary>
public static class PurviewServiceResultRowsHash
{
    private const string HashPrefix = "archivebridge.purview.service-result-rows.v1";

    /// <summary>
    /// Calcula o hash a partir das linhas, ordenadas deterministicamente por
    /// <see cref="PurviewServiceResultRow.RemoteName"/> (Ordinal) — nunca pela ordem de leitura/inserção.
    /// </summary>
    public static Sha256Hash Compute(IReadOnlyList<PurviewServiceResultRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var parts = new List<string> { HashPrefix, rows.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var row in rows.OrderBy(row => row.RemoteName.Value, StringComparer.Ordinal))
        {
            parts.Add(row.RemoteName.Value);
            parts.Add(((int)row.Status).ToString(CultureInfo.InvariantCulture));
            parts.Add(row.ImportedItemCount?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(row.ImportedSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(row.SkippedItemCount?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(row.CorruptedItemCount?.ToString(CultureInfo.InvariantCulture) ?? "null");
        }

        return DeterministicHash.Compute(parts);
    }
}
