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
/// Resultado ESTRUTURADO da validação de um CSV externo (upload), com problemas tipados (código/linha/
/// coluna), a contagem de linhas de dados quando determinável, o total de problemas encontrados e se a
/// lista persistida foi truncada por um teto. A lista de <see cref="Issues"/> já vem em ordem
/// determinística e limitada ao teto informado.
/// </summary>
public sealed record MappingCsvValidation(
    bool IsValid,
    int? RowCount,
    IReadOnlyList<MappingValidationIssue> Issues,
    int IssueCount,
    bool IssuesTruncated);

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
    // Cabeçalho + MaxDataRows + 1 registro-sentinela: basta para decidir "linhas excedidas" sem
    // materializar registros arbitrariamente (defesa de amplificação para entrada de upload).
    private const int MaxParsedRecords = MappingSchema.MaxDataRows + 2;

    /// <summary>Valida o CSV contra a onda autorizada e a política (projeção de mensagens para compatibilidade).</summary>
    public static MappingValidationResult Validate(
        string csvText, MigrationWave wave, ContentCodePage contentCodePage, MappingPolicy policy)
    {
        var validation = ValidateUpload(csvText, wave, contentCodePage, policy, int.MaxValue);
        return validation.IsValid
            ? MappingValidationResult.Success
            : MappingValidationResult.Failure(validation.Issues.Select(issue => issue.Message).ToList());
    }

    /// <summary>
    /// Valida um CSV externo produzindo problemas ESTRUTURADOS (código/linha/coluna), com parsing
    /// LIMITADO (anti-amplificação) e a lista de problemas ordenada deterministicamente e limitada a
    /// <paramref name="maxIssues"/> (os primeiros, na ordem determinística).
    /// </summary>
    public static MappingCsvValidation ValidateUpload(
        string csvText, MigrationWave wave, ContentCodePage contentCodePage, MappingPolicy policy, int maxIssues)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(policy);
        var issues = new List<MappingValidationIssue>();

        if (string.IsNullOrEmpty(csvText))
        {
            issues.Add(Global(MappingValidationIssueCode.Empty, "CSV vazio."));
            return Finalize(issues, null, maxIssues);
        }

        if (csvText[0] == '\uFEFF')
        {
            issues.Add(Global(MappingValidationIssueCode.Bom, "CSV contém BOM; esperado UTF-8 sem BOM."));
        }

        IReadOnlyList<IReadOnlyList<string>> records;
        bool exceeded;
        try
        {
            records = MappingCsvParser.Parse(csvText, MaxParsedRecords, out exceeded);
        }
        catch (MappingCsvFormatException)
        {
            issues.Add(Global(MappingValidationIssueCode.Malformed, "CSV estruturalmente malformado; rejeitado (fail-closed)."));
            return Finalize(issues, null, maxIssues);
        }

        if (records.Count == 0)
        {
            issues.Add(Global(MappingValidationIssueCode.NoRecords, "CSV sem registros."));
            return Finalize(issues, null, maxIssues);
        }

        if (!HeaderMatches(records[0]))
        {
            issues.Add(Global(MappingValidationIssueCode.HeaderInvalid, "Cabeçalho inválido: colunas ou ordem divergentes do esquema."));
            return Finalize(issues, null, maxIssues);
        }

        if (!policy.IsAllowed(contentCodePage))
        {
            issues.Add(Global(MappingValidationIssueCode.CodePageDisallowed, "ContentCodePage fora da política."));
        }

        int? rowCount = null;
        if (exceeded)
        {
            issues.Add(Global(
                MappingValidationIssueCode.RowsExceeded,
                $"Número de linhas de dados excede o limite de {MappingSchema.MaxDataRows}."));
        }
        else
        {
            rowCount = records.Count - 1;
        }

        Dictionary<string, MappingRow> expected;
        try
        {
            expected = BuildExpected(wave, contentCodePage);
        }
        catch (Exception exception) when (
            exception is ArgumentException or MappingGenerationException or MappingCsvInjectionException)
        {
            issues.Add(Global(MappingValidationIssueCode.SourceInconsistent, "Fonte autorizada inconsistente; não é possível validar o CSV."));
            return Finalize(issues, rowCount, maxIssues);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            var line = recordIndex + 1;
            var record = records[recordIndex];
            if (record.Count != MappingSchema.ColumnCount)
            {
                issues.Add(Line(MappingValidationIssueCode.ColumnsInvalid, line, null, $"Linha {line}: número de colunas inválido ({record.Count})."));
                continue;
            }

            for (var column = 0; column < record.Count; column++)
            {
                if (MappingCsvSerializer.StartsWithFormulaTrigger(record[column]))
                {
                    issues.Add(Line(MappingValidationIssueCode.Formula, line, column, $"Linha {line}: coluna {column} inicia com caractere de fórmula."));
                }
            }

            ValidateFixedColumns(record, line, issues);
            ValidateAuthorizedRow(record, line, expected, seen, issues);
        }

        var missing = expected.Keys.Count(name => !seen.Contains(name));
        if (missing > 0)
        {
            issues.Add(Global(
                MappingValidationIssueCode.AuthorizedMissing,
                $"{missing} linha(s) autorizada(s) ausente(s) no CSV (linhas descartadas)."));
        }

        return Finalize(issues, rowCount, maxIssues);
    }

    private static MappingValidationIssue Global(string code, string message) => new(code, null, null, message);

    private static MappingValidationIssue Line(string code, int line, int? column, string message) =>
        new(code, line, column, message);

    // Ordenação determinística (§20): 1) globais/preflight (sem linha) antes das de linha; 2) linha ASC;
    // 3) coluna ASC (sem coluna antes das com coluna); 4) ordinal fixo do código. Depois trunca ao teto.
    private static MappingCsvValidation Finalize(List<MappingValidationIssue> issues, int? rowCount, int maxIssues)
    {
        var ordered = issues
            .OrderBy(issue => issue.LineNumber is null ? 0 : 1)
            .ThenBy(issue => issue.LineNumber ?? 0)
            .ThenBy(issue => issue.ColumnIndex ?? -1)
            .ThenBy(issue => MappingValidationIssueCode.Ordinal(issue.Code))
            .ToList();

        var total = ordered.Count;
        var truncated = total > maxIssues;
        IReadOnlyList<MappingValidationIssue> persisted = truncated ? ordered.Take(maxIssues).ToList() : ordered;
        return new MappingCsvValidation(total == 0, rowCount, persisted, total, truncated);
    }

    private static void ValidateFixedColumns(IReadOnlyList<string> record, int line, List<MappingValidationIssue> issues)
    {
        if (!string.Equals(record[0], MappingSchema.ExchangeWorkload, StringComparison.Ordinal))
        {
            issues.Add(Line(MappingValidationIssueCode.WorkloadInvalid, line, 0, $"Linha {line}: Workload deve ser '{MappingSchema.ExchangeWorkload}'."));
        }

        if (!string.Equals(record[4], MappingSchema.ArchiveTrue, StringComparison.Ordinal))
        {
            issues.Add(Line(MappingValidationIssueCode.ArchiveInvalid, line, 4, $"Linha {line}: IsArchive deve ser '{MappingSchema.ArchiveTrue}'."));
        }

        if (record[7].Length != 0 || record[8].Length != 0 || record[9].Length != 0)
        {
            issues.Add(Line(MappingValidationIssueCode.SharePointNonEmpty, line, 7, $"Linha {line}: colunas SharePoint devem estar vazias."));
        }
    }

    private static void ValidateAuthorizedRow(
        IReadOnlyList<string> record,
        int line,
        Dictionary<string, MappingRow> expected,
        HashSet<string> seen,
        List<MappingValidationIssue> issues)
    {
        var name = record[2];
        if (name.Length == 0)
        {
            issues.Add(Line(MappingValidationIssueCode.NameMissing, line, 2, $"Linha {line}: Name ausente."));
            return;
        }

        if (!seen.Add(name))
        {
            issues.Add(Line(MappingValidationIssueCode.PstDuplicate, line, 2, $"Linha {line}: PST duplicado."));
            return;
        }

        if (!expected.TryGetValue(name, out var expectedRow))
        {
            issues.Add(Line(MappingValidationIssueCode.PstUnauthorized, line, 2, $"Linha {line}: PST fora do manifesto autorizado."));
            return;
        }

        var matches =
            string.Equals(record[1], expectedRow.FilePath, StringComparison.Ordinal) &&
            string.Equals(record[3], expectedRow.Mailbox, StringComparison.Ordinal) &&
            string.Equals(record[5], expectedRow.TargetRootFolder.Value, StringComparison.Ordinal) &&
            string.Equals(record[6], expectedRow.ContentCodePage.ToCsvField(), StringComparison.Ordinal);
        if (!matches)
        {
            issues.Add(Line(MappingValidationIssueCode.RowDivergent, line, null, $"Linha {line}: divergência em relação à fonte autorizada."));
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
