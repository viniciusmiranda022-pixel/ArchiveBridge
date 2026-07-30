using System.Text;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Serializa linhas de mapping em CSV RFC 4180 de forma segura contra injeção de fórmula. Um campo
/// cujo valor comece por um caractere interpretável como fórmula (<c>= + - @</c>, tabulação ou CR)
/// faz a serialização falhar (fail-closed) — nunca reescreve o valor autorizado. Campos com vírgula
/// ou aspas são citados de forma reversível (sem alterar a semântica). Sem BOM; separador CRLF.
/// </summary>
internal static class MappingCsvSerializer
{
    /// <summary>Terminador de registro (RFC 4180).</summary>
    internal const string RecordSeparator = "\r\n";

    private const char Quote = '"';

    /// <summary>Serializa cabeçalho + linhas em um documento CSV UTF-8 (texto).</summary>
    public static string Serialize(IReadOnlyList<MappingRow> rows)
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

    /// <summary>
    /// Indica se um valor começa por caractere interpretável como fórmula. Usado tanto na geração
    /// (fail-closed) quanto na validação de CSV externo (detecção de adulteração).
    /// </summary>
    public static bool StartsWithFormulaTrigger(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
    }

    private static string SerializeRow(MappingRow row) =>
        string.Join(
            ',',
            EncodeField(MappingSchema.ExchangeWorkload),
            EncodeField(row.FilePath),
            EncodeField(row.Name),
            EncodeField(row.Mailbox),
            EncodeField(MappingSchema.ArchiveTrue),
            EncodeField(row.TargetRootFolder.Value),
            EncodeField(row.ContentCodePage.ToCsvField()),
            EncodeField(string.Empty),
            EncodeField(string.Empty),
            EncodeField(string.Empty));

    private static string EncodeField(string value)
    {
        if (StartsWithFormulaTrigger(value))
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
