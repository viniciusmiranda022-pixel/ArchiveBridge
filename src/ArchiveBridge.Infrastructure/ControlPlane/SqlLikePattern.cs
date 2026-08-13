namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Constrói padrões <c>LIKE</c> de PREFIXO seguros para as buscas paginadas do portal. O valor do usuário é
/// tratado como DADO literal: os metacaracteres de <c>LIKE</c> (<c>\</c>, <c>%</c>, <c>_</c>, <c>[</c>) são
/// escapados e o curinga de prefixo (<c>%</c>) é anexado pelo servidor. A consulta usa
/// <c>col LIKE @p ESCAPE N'\'</c>. Nenhum curinga arbitrário do usuário é interpretado como sintaxe.
/// </summary>
internal static class SqlLikePattern
{
    /// <summary>Escapa o valor e anexa o curinga de prefixo (<c>%</c>). O caractere de escape é a barra invertida.</summary>
    public static string EscapedPrefix(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
        return escaped + "%";
    }
}
