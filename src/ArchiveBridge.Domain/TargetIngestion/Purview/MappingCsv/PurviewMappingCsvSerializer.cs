using System.Text;
using ArchiveBridge.Domain.Mapping;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Serializa <see cref="PurviewMappingRow"/> no CSV RFC 4180 do Purview Network Upload, reaproveitando o
/// cabeçalho/esquema de 10 colunas já definido em <see cref="MappingSchema"/> (mesmo arquivo, mesma
/// ordem/grafia exigida pelo Purview — item 8 "reuse where it already satisfies") e a mesma detecção de
/// gatilho de fórmula (<see cref="MappingCsvSerializer.StartsWithFormulaTrigger"/>) usada pelo mapping
/// genérico do Slice 2. Difere do mapping genérico em dois pontos exigidos pelo work order (item 5):
/// <c>IsArchive</c> é o valor RESOLVIDO por linha (nunca fixo em <c>TRUE</c>), e <c>ContentCodePage</c> +
/// as três colunas SharePoint são SEMPRE vazias (caminho Exchange/PST puro, sem policy de code page). Sem
/// BOM; separador CRLF; um campo cujo valor comece por caractere interpretável como fórmula faz a
/// serialização falhar (fail-closed) — nunca reescreve o valor autorizado.
/// </summary>
internal static class PurviewMappingCsvSerializer
{
    private const string RecordSeparator = "\r\n";
    private const string ArchiveTrue = "TRUE";
    private const string ArchiveFalse = "FALSE";
    private const char Quote = '"';

    /// <summary>Serializa cabeçalho + linhas em um documento CSV UTF-8 (texto).</summary>
    public static string Serialize(IReadOnlyList<PurviewMappingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        builder.Append(MappingSchema.HeaderLine).Append(RecordSeparator);
        foreach (var row in rows)
        {
            builder.Append(SerializeRow(row)).Append(RecordSeparator);
        }

        return builder.ToString();
    }

    private static string SerializeRow(PurviewMappingRow row) =>
        string.Join(
            ',',
            EncodeField(MappingSchema.ExchangeWorkload),
            EncodeField(row.FilePath),
            EncodeField(row.Name),
            EncodeField(row.Mailbox),
            EncodeField(row.IsArchive ? ArchiveTrue : ArchiveFalse),
            EncodeField(row.TargetRootFolder.Value),
            EncodeField(string.Empty), // ContentCodePage — sempre vazio no caminho Exchange/PST (item 5).
            EncodeField(string.Empty), // SPFileContainer
            EncodeField(string.Empty), // SPManifestContainer
            EncodeField(string.Empty)); // SPSiteUrl

    private static string EncodeField(string value)
    {
        if (MappingCsvSerializer.StartsWithFormulaTrigger(value))
        {
            // Não prefixa nem reescreve: recusa emitir o valor autorizado alterado.
            throw new MappingCsvInjectionException(
                "Um valor autorizado começaria por caractere de fórmula; geração recusada (fail-closed).");
        }

        var needsQuoting =
            value.Contains(',', StringComparison.Ordinal) ||
            value.Contains(Quote, StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal);

        if (!needsQuoting)
        {
            return value;
        }

        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return string.Concat("\"", escaped, "\"");
    }
}
