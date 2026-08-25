using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Resultado de uma análise bem-sucedida: as linhas normalizadas e, quando o relatório declarou
/// explicitamente sua própria contagem total (diretiva <c>#TotalRows:</c>), esse valor — usado depois pela
/// correlação (AB-I6-001 item 8) para decidir se um conjunto extra/ausente em relação à cadeia canônica
/// deve falhar fechado (o formato AFIRMOU completude) ou apenas marcar incompletude (o formato nunca
/// afirmou cobrir todos os PSTs da onda).
/// </summary>
public sealed record PurviewServiceResultReportParseResult(
    IReadOnlyList<PurviewServiceResultRow> Rows, int? DeclaredTotalRows);

/// <summary>
/// Parser PURO de domínio para o validation report / service result do Purview (AB-I6-001 item 6): trata o
/// arquivo anexado como ENTRADA HOSTIL — nunca confia em nome/path/extensão do chamador, nunca executa
/// conteúdo, e aplica limites estritos de tamanho/linhas/campos/encoding. Este NÃO é um parser do formato
/// interno (não documentado/certificado) do Purview: é o esquema PRÓPRIO do ArchiveBridge — um pequeno
/// conjunto fixo de colunas reconhecidas que o operador transcreve/exporta a partir do portal — para o
/// material deste Passo (item 7: "normalizar somente os campos necessários"). Qualquer desvio do formato
/// (coluna desconhecida, campo em excesso/faltando, encoding inválido, linha/tamanho em excesso, valor
/// numérico malformado) recusa o relatório INTEIRO (fail-closed) — nunca produz um resultado parcial.
/// </summary>
public static partial class PurviewServiceResultReportParser
{
    /// <summary>Tamanho máximo do relatório bruto, em bytes.</summary>
    public const int MaxReportBytes = 2_000_000;

    /// <summary>Quantidade máxima de linhas de dados (bem acima do limite de 500 PSTs do mapping CSV).</summary>
    public const int MaxDataRows = 2_000;

    /// <summary>Tamanho máximo de um campo individual, em caracteres.</summary>
    public const int MaxFieldLength = 400;

    private const string IdentityColumn = "RemotePstName";
    private const string StatusColumn = "Status";
    private const string ImportedItemCountColumn = "ImportedItemCount";
    private const string ImportedSizeBytesColumn = "ImportedSizeBytes";
    private const string SkippedItemCountColumn = "SkippedItemCount";
    private const string CorruptedItemCountColumn = "CorruptedItemCount";

    private static readonly string[] RecognizedColumnsInOrder =
    [
        IdentityColumn, StatusColumn, ImportedItemCountColumn, ImportedSizeBytesColumn, SkippedItemCountColumn, CorruptedItemCountColumn,
    ];

    [GeneratedRegex(@"^p_[0-9a-f]{32}_part\d{3}\.pst$", RegexOptions.CultureInvariant)]
    private static partial Regex RemotePstNamePattern();

    [GeneratedRegex(@"^#TotalRows:(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TotalRowsDirectivePattern();

    /// <summary>Analisa o conteúdo bruto, bounded e fail-closed.</summary>
    /// <exception cref="PurviewServiceResultParsingException">Qualquer desvio do formato exigido.</exception>
    public static PurviewServiceResultReportParseResult Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            throw new PurviewServiceResultParsingException("O relatório está vazio (fail-closed).");
        }

        if (bytes.Length > MaxReportBytes)
        {
            throw new PurviewServiceResultParsingException(
                $"O relatório excede o limite de {MaxReportBytes} bytes (fail-closed).");
        }

        var span = bytes.Span;
        foreach (var b in span)
        {
            if (b == 0)
            {
                throw new PurviewServiceResultParsingException("O relatório contém um byte NUL — conteúdo não textual recusado (fail-closed).");
            }
        }

        string text;
        try
        {
            // Encoder/decoder estritos: qualquer sequência de bytes inválida para UTF-8 é recusada, nunca
            // substituída silenciosamente por um caractere de reposição.
            var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = decoder.GetString(StripUtf8Bom(span));
        }
        catch (DecoderFallbackException exception)
        {
            throw new PurviewServiceResultParsingException("O relatório não é UTF-8 válido (fail-closed).", exception);
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lineList = new List<string>(lines);
        while (lineList.Count > 0 && lineList[^1].Length == 0)
        {
            lineList.RemoveAt(lineList.Count - 1);
        }

        if (lineList.Count == 0)
        {
            throw new PurviewServiceResultParsingException("O relatório não contém nenhuma linha (fail-closed).");
        }

        var cursor = 0;
        int? declaredTotalRows = null;
        if (lineList[0].StartsWith('#'))
        {
            var directiveMatch = TotalRowsDirectivePattern().Match(lineList[0]);
            if (!directiveMatch.Success)
            {
                throw new PurviewServiceResultParsingException(
                    "A linha de diretiva do relatório não corresponde ao formato '#TotalRows:<N>' reconhecido (fail-closed).");
            }

            declaredTotalRows = int.Parse(directiveMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            cursor = 1;
        }

        if (cursor >= lineList.Count)
        {
            throw new PurviewServiceResultParsingException("O relatório não contém uma linha de cabeçalho (fail-closed).");
        }

        var columnIndexByName = ParseHeader(lineList[cursor]);
        cursor++;

        var dataLines = lineList.Skip(cursor).ToList();
        if (dataLines.Count == 0)
        {
            throw new PurviewServiceResultParsingException("O relatório não contém nenhuma linha de dados (fail-closed).");
        }

        if (dataLines.Count > MaxDataRows)
        {
            throw new PurviewServiceResultParsingException(
                $"O relatório excede o limite de {MaxDataRows} linhas de dados (fail-closed).");
        }

        var rows = new List<PurviewServiceResultRow>(dataLines.Count);
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in dataLines)
        {
            rows.Add(ParseRow(line, columnIndexByName, seenIdentities));
        }

        if (declaredTotalRows is { } declared && declared != rows.Count)
        {
            throw new PurviewServiceResultParsingException(
                $"O relatório declara {declared} linha(s) totais (diretiva #TotalRows) mas contém {rows.Count} — inconsistência estrutural (fail-closed).");
        }

        return new PurviewServiceResultReportParseResult(rows, declaredTotalRows);
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        return span.Length >= 3 && span[..3].SequenceEqual(bom) ? span[3..] : span;
    }

    private static Dictionary<string, int> ParseHeader(string headerLine)
    {
        var fields = SplitFields(headerLine);
        var columnIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fields.Count; index++)
        {
            var name = fields[index];
            if (!RecognizedColumnsInOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new PurviewServiceResultParsingException(
                    $"Coluna de cabeçalho desconhecida: '{name}' (fail-closed — apenas colunas reconhecidas são aceitas).");
            }

            if (!columnIndexByName.TryAdd(name, index))
            {
                throw new PurviewServiceResultParsingException($"Coluna de cabeçalho duplicada: '{name}' (fail-closed).");
            }
        }

        if (!columnIndexByName.ContainsKey(IdentityColumn))
        {
            throw new PurviewServiceResultParsingException(
                $"O cabeçalho não contém a coluna de identidade obrigatória '{IdentityColumn}' (fail-closed).");
        }

        return columnIndexByName;
    }

    private static PurviewServiceResultRow ParseRow(string line, Dictionary<string, int> columnIndexByName, HashSet<string> seenIdentities)
    {
        var fields = SplitFields(line);
        if (fields.Count != columnIndexByName.Count)
        {
            throw new PurviewServiceResultParsingException(
                $"Uma linha de dados tem {fields.Count} campo(s), mas o cabeçalho declara {columnIndexByName.Count} (fail-closed).");
        }

        var identityRaw = fields[columnIndexByName[IdentityColumn]];
        if (!RemotePstNamePattern().IsMatch(identityRaw))
        {
            throw new PurviewServiceResultParsingException(
                "Uma linha de dados referencia um nome remoto que não corresponde ao formato determinístico esperado (fail-closed).");
        }

        if (!seenIdentities.Add(identityRaw))
        {
            throw new PurviewServiceResultParsingException(
                $"O relatório contém mais de uma linha para o mesmo PST remoto '{identityRaw}' (fail-closed).");
        }

        var remoteName = PurviewRemotePstName.FromPersistedValue(identityRaw);
        var status = ParseStatus(fields, columnIndexByName);
        var importedItemCount = ParseNonNegativeLongOrNull(fields, columnIndexByName, ImportedItemCountColumn);
        var importedSizeBytes = ParseNonNegativeLongOrNull(fields, columnIndexByName, ImportedSizeBytesColumn);
        var skippedItemCount = ParseNonNegativeLongOrNull(fields, columnIndexByName, SkippedItemCountColumn);
        var corruptedItemCount = ParseNonNegativeLongOrNull(fields, columnIndexByName, CorruptedItemCountColumn);

        return new PurviewServiceResultRow(remoteName, status, importedItemCount, importedSizeBytes, skippedItemCount, corruptedItemCount);
    }

    private static PurviewServiceResultRowStatus ParseStatus(List<string> fields, Dictionary<string, int> columnIndexByName)
    {
        if (!columnIndexByName.TryGetValue(StatusColumn, out var index))
        {
            return PurviewServiceResultRowStatus.Unknown;
        }

        var value = fields[index];
        return value.ToUpperInvariant() switch
        {
            "SUCCEEDED" or "SUCCESS" => PurviewServiceResultRowStatus.Succeeded,
            "FAILED" or "FAILURE" or "ERROR" => PurviewServiceResultRowStatus.Failed,
            "SKIPPEDORCORRUPTED" or "SKIPPED" or "CORRUPTED" => PurviewServiceResultRowStatus.SkippedOrCorrupted,
            _ => PurviewServiceResultRowStatus.Unknown,
        };
    }

    private static long? ParseNonNegativeLongOrNull(List<string> fields, Dictionary<string, int> columnIndexByName, string columnName)
    {
        if (!columnIndexByName.TryGetValue(columnName, out var index))
        {
            // A coluna nem existe no relatório: Unknown/NotReported (item 7) — nunca zero.
            return null;
        }

        var value = fields[index];
        if (value.Length == 0)
        {
            // A coluna existe, mas esta linha não preencheu o valor: Unknown/NotReported — nunca zero.
            return null;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new PurviewServiceResultParsingException(
                $"O campo '{columnName}' contém um valor não numérico/negativo malformado (fail-closed).");
        }

        return parsed;
    }

    private static List<string> SplitFields(string line)
    {
        if (line.Length > MaxFieldLength * RecognizedColumnsInOrder.Length)
        {
            throw new PurviewServiceResultParsingException("Uma linha do relatório excede o tamanho combinado máximo esperado (fail-closed).");
        }

        var fields = line.Split(',');
        foreach (var field in fields)
        {
            if (field.Length > MaxFieldLength)
            {
                throw new PurviewServiceResultParsingException($"Um campo excede {MaxFieldLength} caracteres (fail-closed).");
            }

            foreach (var character in field)
            {
                if (char.IsControl(character))
                {
                    throw new PurviewServiceResultParsingException("Um campo contém caractere de controle (fail-closed).");
                }
            }
        }

        return fields.Select(field => field.Trim()).ToList();
    }
}
