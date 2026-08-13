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

    /// <summary>
    /// Tamanho máximo (em caracteres) que <see cref="EscapedPrefix"/> pode produzir para uma entrada de até
    /// <paramref name="rawMaxLength"/> caracteres. Cada caractere pode ser um metacaractere de <c>LIKE</c>
    /// (<c>\ % _ [</c>) e virar dois caracteres, e o servidor anexa um <c>%</c> de prefixo — logo o pior caso é
    /// <c>rawMaxLength * 2 + 1</c>. O parâmetro SQL <c>NVARCHAR</c> deve ter ESTE tamanho para nunca truncar o
    /// padrão escapado (truncar quebraria o escaping e reintroduziria semântica de curinga). <c>checked</c>
    /// documenta que a expansão é determinística e não transborda.
    /// </summary>
    public static int MaxEscapedPrefixLength(int rawMaxLength) => checked((rawMaxLength * 2) + 1);
}
