namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Esquema fixo do CSV de mapping (importação PST → Exchange Online). Exatamente 10 colunas, na
/// ordem e com os nomes exigidos. Constantes de negócio (workload, flag de archive, limite de
/// linhas) e o cabeçalho canônico. Qualquer desvio do esquema é rejeitado (fail-closed).
/// </summary>
public static class MappingSchema
{
    /// <summary>Número exato de colunas do mapping.</summary>
    public const int ColumnCount = 10;

    /// <summary>Máximo de linhas de dados por CSV (sem contar o cabeçalho).</summary>
    public const int MaxDataRows = 500;

    /// <summary>Único workload aceito nesta plataforma.</summary>
    public const string ExchangeWorkload = "Exchange";

    /// <summary>Valor obrigatório da coluna <c>IsArchive</c> (importa para o archive).</summary>
    public const string ArchiveTrue = "TRUE";

    /// <summary>As 10 colunas do mapping, na ordem canônica exigida pelo serviço de importação.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "Workload",
        "FilePath",
        "Name",
        "Mailbox",
        "IsArchive",
        "TargetRootFolder",
        "ContentCodePage",
        "SPFileContainer",
        "SPManifestContainer",
        "SPSiteUrl",
    ];

    /// <summary>Linha de cabeçalho canônica (colunas separadas por vírgula, sem espaços).</summary>
    public static string HeaderLine { get; } = string.Join(',', Columns);
}
