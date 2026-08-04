using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Waves;

/// <summary>
/// Identidade canônica e resolvida do archive de destino — a <b>chave de agrupamento</b> do
/// planejamento de capacidade. Não é a string de exibição: é normalizada de forma invariante de
/// cultura para que variações de caixa (<c>User@contoso.com</c> vs <c>user@contoso.com</c>) não
/// dividam artificialmente o volume. Aliases distintos do mesmo archive são unificados quando um
/// resolvedor a montante fornece o mesmo identificador explicitamente (ver <see cref="ArchiveRef"/>).
/// A resolução real de aliases por diretório é de um adaptador de slice futuro; aqui a regra apenas
/// deixa de depender da string de apresentação.
/// </summary>
public readonly record struct TargetArchiveId
{
    private const int MaxLength = 320;

    /// <summary>Cria uma identidade canônica a partir de um valor já resolvido.</summary>
    public TargetArchiveId(string value)
    {
        var normalized = TextValue.Require(value, nameof(value), MaxLength);
        if (normalized.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException("TargetArchiveId não pode conter espaços.", nameof(value));
        }

        // Canonicalização invariante (ToUpperInvariant é a forma recomendada para chaves): remove a
        // sensibilidade a caixa, impedindo divisão artificial do volume por variação textual.
        Value = normalized.ToUpperInvariant();
    }

    /// <summary>Valor canônico (case-insensitive) usado como chave de agrupamento.</summary>
    public string Value { get; }

    /// <summary>
    /// Deriva a identidade canônica a partir de uma mailbox/UPN (resolvedor padrão: apenas caixa).
    /// Um resolvedor a montante pode, em vez disso, construir a identidade explicitamente para
    /// unificar aliases.
    /// </summary>
    public static TargetArchiveId FromMailbox(string mailbox) => new(mailbox);
}
