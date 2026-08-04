using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Resultado de validação de um CSV de mapping. As mensagens são propositalmente livres de PII —
/// referenciam índices de linha/coluna e contagens, nunca mailboxes, caminhos ou nomes de PST.
/// </summary>
public sealed record MappingValidationResult
{
    private MappingValidationResult(bool isValid, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    /// <summary>Verdadeiro se o CSV está conforme (nenhum erro).</summary>
    public bool IsValid { get; }

    /// <summary>Erros encontrados (vazio quando válido); mensagens sem PII.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Resultado válido (sem erros).</summary>
    public static MappingValidationResult Success { get; } = new(true, []);

    /// <summary>Cria um resultado inválido com os erros informados (copiados).</summary>
    public static MappingValidationResult Failure(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new MappingValidationResult(false, [.. errors]);
    }
}

/// <summary>
/// Valida um CSV de mapping (texto externo, possivelmente adulterado) contra a fonte autorizada — a
/// onda aprovada. Verifica, de forma fail-closed: cabeçalho exato (10 colunas, ordem); sem BOM;
/// ≤ 500 linhas; cada linha com 10 colunas; <c>Workload=Exchange</c>; <c>IsArchive=TRUE</c>; colunas
/// SharePoint vazias; ausência de caractere de fórmula; nome no manifesto autorizado e único; e
/// coincidência exata de FilePath/Mailbox/TargetRootFolder/ContentCodePage com a fonte. Também
/// detecta linhas autorizadas ausentes (descartadas). Nenhuma mensagem expõe PII.
/// </summary>
public static class MappingCsvValidator
{
    /// <summary>Valida o CSV contra a onda autorizada e a política.</summary>
    public static MappingValidationResult Validate(
        string csvText, MigrationWave wave, ContentCodePage contentCodePage, MappingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(policy);
        var errors = new List<string>();

        if (string.IsNullOrEmpty(csvText))
        {
            errors.Add("CSV vazio.");
            return MappingValidationResult.Failure(errors);
        }

        if (csvText[0] == '\uFEFF')
        {
            errors.Add("CSV contém BOM; esperado UTF-8 sem BOM.");
        }

        IReadOnlyList<IReadOnlyList<string>> records;
        try
        {
            records = MappingCsvParser.Parse(csvText);
        }
        catch (MappingCsvFormatException)
        {
            errors.Add("CSV estruturalmente malformado; rejeitado (fail-closed).");
            return MappingValidationResult.Failure(errors);
        }

        if (records.Count == 0)
        {
            errors.Add("CSV sem registros.");
            return MappingValidationResult.Failure(errors);
        }

        if (!HeaderMatches(records[0]))
        {
            errors.Add("Cabeçalho inválido: colunas ou ordem divergentes do esquema.");
            return MappingValidationResult.Failure(errors);
        }

        if (!policy.IsAllowed(contentCodePage))
        {
            errors.Add("ContentCodePage fora da política.");
        }

        var dataRecordCount = records.Count - 1;
        if (dataRecordCount > MappingSchema.MaxDataRows)
        {
            errors.Add($"Número de linhas de dados ({dataRecordCount}) excede o limite de {MappingSchema.MaxDataRows}.");
        }

        Dictionary<string, MappingRow> expected;
        try
        {
            expected = BuildExpected(wave, contentCodePage);
        }
        catch (Exception exception) when (
            exception is ArgumentException or MappingGenerationException or MappingCsvInjectionException)
        {
            errors.Add("Fonte autorizada inconsistente; não é possível validar o CSV.");
            return MappingValidationResult.Failure(errors);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            var line = recordIndex + 1;
            var record = records[recordIndex];
            if (record.Count != MappingSchema.ColumnCount)
            {
                errors.Add($"Linha {line}: número de colunas inválido ({record.Count}).");
                continue;
            }

            for (var column = 0; column < record.Count; column++)
            {
                if (MappingCsvSerializer.StartsWithFormulaTrigger(record[column]))
                {
                    errors.Add($"Linha {line}: coluna {column} inicia com caractere de fórmula.");
                }
            }

            ValidateFixedColumns(record, line, errors);
            ValidateAuthorizedRow(record, line, expected, seen, errors);
        }

        var missing = expected.Keys.Count(name => !seen.Contains(name));
        if (missing > 0)
        {
            errors.Add($"{missing} linha(s) autorizada(s) ausente(s) no CSV (linhas descartadas).");
        }

        return errors.Count == 0 ? MappingValidationResult.Success : MappingValidationResult.Failure(errors);
    }

    private static void ValidateFixedColumns(IReadOnlyList<string> record, int line, List<string> errors)
    {
        if (!string.Equals(record[0], MappingSchema.ExchangeWorkload, StringComparison.Ordinal))
        {
            errors.Add($"Linha {line}: Workload deve ser '{MappingSchema.ExchangeWorkload}'.");
        }

        if (!string.Equals(record[4], MappingSchema.ArchiveTrue, StringComparison.Ordinal))
        {
            errors.Add($"Linha {line}: IsArchive deve ser '{MappingSchema.ArchiveTrue}'.");
        }

        if (record[7].Length != 0 || record[8].Length != 0 || record[9].Length != 0)
        {
            errors.Add($"Linha {line}: colunas SharePoint devem estar vazias.");
        }
    }

    private static void ValidateAuthorizedRow(
        IReadOnlyList<string> record,
        int line,
        Dictionary<string, MappingRow> expected,
        HashSet<string> seen,
        List<string> errors)
    {
        var name = record[2];
        if (name.Length == 0)
        {
            errors.Add($"Linha {line}: Name ausente.");
            return;
        }

        if (!seen.Add(name))
        {
            errors.Add($"Linha {line}: PST duplicado.");
            return;
        }

        if (!expected.TryGetValue(name, out var expectedRow))
        {
            errors.Add($"Linha {line}: PST fora do manifesto autorizado.");
            return;
        }

        var matches =
            string.Equals(record[1], expectedRow.FilePath, StringComparison.Ordinal) &&
            string.Equals(record[3], expectedRow.Mailbox, StringComparison.Ordinal) &&
            string.Equals(record[5], expectedRow.TargetRootFolder.Value, StringComparison.Ordinal) &&
            string.Equals(record[6], expectedRow.ContentCodePage.ToCsvField(), StringComparison.Ordinal);
        if (!matches)
        {
            errors.Add($"Linha {line}: divergência em relação à fonte autorizada.");
        }
    }

    private static Dictionary<string, MappingRow> BuildExpected(MigrationWave wave, ContentCodePage contentCodePage)
    {
        var map = new Dictionary<string, MappingRow>(StringComparer.Ordinal);
        foreach (var entry in wave.Selection.Entries)
        {
            var row = MappingRow.Create(entry, wave.TargetRootFolder, contentCodePage);
            map[row.Name] = row;
        }

        return map;
    }

    private static bool HeaderMatches(IReadOnlyList<string> header)
    {
        if (header.Count != MappingSchema.ColumnCount)
        {
            return false;
        }

        for (var column = 0; column < header.Count; column++)
        {
            if (!string.Equals(header[column], MappingSchema.Columns[column], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
