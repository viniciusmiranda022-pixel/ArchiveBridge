namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Um problema estruturado de validação de um CSV de mapping. É deliberadamente SEM PII: referencia
/// apenas um código fechado, opcionalmente o número da linha (1-based, incluindo o cabeçalho) e o
/// índice da coluna (0-based), e uma mensagem genérica de exibição — nunca mailbox, caminho, PST,
/// valor de célula bruto ou nome de arquivo do cliente.
/// </summary>
public sealed record MappingValidationIssue(string Code, int? LineNumber, int? ColumnIndex, string Message);

/// <summary>
/// Conjunto FECHADO e estável dos códigos de problema de validação de CSV de mapping. Os códigos são
/// contratos: não mudam de valor entre versões (persistidos em <c>mapping_validation_issues</c>). A
/// ordem em <see cref="All"/> também define a precedência determinística usada para desempatar problemas
/// globais (sem linha) e problemas na mesma linha/coluna.
/// </summary>
public static class MappingValidationIssueCode
{
    /// <summary>Conteúdo vazio.</summary>
    public const string Empty = "csv.empty";

    /// <summary>Byte Order Mark presente (esperado UTF-8 sem BOM).</summary>
    public const string Bom = "csv.bom";

    /// <summary>Bytes não decodificáveis como UTF-8 estrito (ou encoding não-UTF-8).</summary>
    public const string InvalidEncoding = "csv.invalid-encoding";

    /// <summary>Estrutura CSV malformada (aspas/limites de registro inválidos).</summary>
    public const string Malformed = "csv.malformed";

    /// <summary>Sem registros após o parsing.</summary>
    public const string NoRecords = "csv.no-records";

    /// <summary>Cabeçalho divergente do esquema (colunas/ordem/nome/case).</summary>
    public const string HeaderInvalid = "csv.header.invalid";

    /// <summary>ContentCodePage fora da política.</summary>
    public const string CodePageDisallowed = "csv.codepage.disallowed";

    /// <summary>Número de linhas de dados excede o limite do esquema.</summary>
    public const string RowsExceeded = "csv.rows.exceeded";

    /// <summary>Fonte autorizada inconsistente; não é possível validar.</summary>
    public const string SourceInconsistent = "csv.source.inconsistent";

    /// <summary>Número de colunas da linha inválido.</summary>
    public const string ColumnsInvalid = "csv.columns.invalid";

    /// <summary>Valor inicia com caractere interpretável como fórmula.</summary>
    public const string Formula = "csv.formula";

    /// <summary>Workload diferente de Exchange.</summary>
    public const string WorkloadInvalid = "csv.workload.invalid";

    /// <summary>IsArchive diferente de TRUE.</summary>
    public const string ArchiveInvalid = "csv.archive.invalid";

    /// <summary>Colunas SharePoint não vazias.</summary>
    public const string SharePointNonEmpty = "csv.sharepoint.nonempty";

    /// <summary>Name (PST) ausente.</summary>
    public const string NameMissing = "csv.name.missing";

    /// <summary>PST duplicado.</summary>
    public const string PstDuplicate = "csv.pst.duplicate";

    /// <summary>PST fora do manifesto autorizado.</summary>
    public const string PstUnauthorized = "csv.pst.unauthorized";

    /// <summary>Linha divergente da fonte autorizada (FilePath/Mailbox/TargetRootFolder/ContentCodePage).</summary>
    public const string RowDivergent = "csv.row.divergent";

    /// <summary>Linha(s) autorizada(s) ausente(s) no CSV.</summary>
    public const string AuthorizedMissing = "csv.authorized.missing";

    /// <summary>Ordem canônica de precedência dos códigos (usada no desempate determinístico).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Empty,
        Bom,
        InvalidEncoding,
        Malformed,
        NoRecords,
        HeaderInvalid,
        CodePageDisallowed,
        RowsExceeded,
        SourceInconsistent,
        ColumnsInvalid,
        Formula,
        WorkloadInvalid,
        ArchiveInvalid,
        SharePointNonEmpty,
        NameMissing,
        PstDuplicate,
        PstUnauthorized,
        RowDivergent,
        AuthorizedMissing,
    ];

    /// <summary>Ordinal de precedência do código (para ordenação determinística). Códigos desconhecidos vão ao fim.</summary>
    public static int Ordinal(string code)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], code, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return All.Count;
    }
}
