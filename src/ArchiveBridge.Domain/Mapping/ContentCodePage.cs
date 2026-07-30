using System.Globalization;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Code page de conteúdo (coluna <c>ContentCodePage</c>). Um code page Windows numérico válido
/// (1..65535; 65001 = UTF-8). Não é conteúdo de e-mail nem PII — é apenas metadado de decodificação.
/// A aceitação real depende da política do projeto (ver <see cref="MappingPolicy"/>).
/// </summary>
public readonly record struct ContentCodePage
{
    /// <summary>Cria um code page válido.</summary>
    public ContentCodePage(int value)
    {
        if (value is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "ContentCodePage deve ser um code page Windows válido (1..65535).");
        }

        Value = value;
    }

    /// <summary>Valor numérico do code page.</summary>
    public int Value { get; }

    /// <summary>Representação para o campo do CSV (invariável de cultura).</summary>
    public string ToCsvField() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Política de geração do mapping. Define os code pages de conteúdo aceitos e o padrão. É a fonte
/// única da regra "ContentCodePage conforme política" — code pages fora da lista são rejeitados
/// (fail-closed). Não contém segredo, SAS nem token.
/// </summary>
public sealed class MappingPolicy
{
    private readonly HashSet<int> allowedContentCodePages;

    /// <summary>Cria a política com os code pages aceitos e o padrão (que deve estar na lista).</summary>
    public MappingPolicy(IEnumerable<int> allowedContentCodePages, ContentCodePage defaultContentCodePage)
    {
        ArgumentNullException.ThrowIfNull(allowedContentCodePages);
        this.allowedContentCodePages = [.. allowedContentCodePages];
        if (this.allowedContentCodePages.Count == 0)
        {
            throw new ArgumentException(
                "A política deve permitir ao menos um ContentCodePage.", nameof(allowedContentCodePages));
        }

        if (!this.allowedContentCodePages.Contains(defaultContentCodePage.Value))
        {
            throw new ArgumentException(
                "O ContentCodePage padrão deve estar entre os permitidos.", nameof(defaultContentCodePage));
        }

        DefaultContentCodePage = defaultContentCodePage;
    }

    /// <summary>Code page padrão quando nenhum é especificado.</summary>
    public ContentCodePage DefaultContentCodePage { get; }

    /// <summary>Code pages aceitos por esta política.</summary>
    public IReadOnlyCollection<int> AllowedContentCodePages => allowedContentCodePages;

    /// <summary>
    /// Política conservadora padrão: 1252 (Windows-1252, padrão) e 65001 (UTF-8). Deliberadamente
    /// mínima; ampliações exigem decisão explícita, não configuração silenciosa.
    /// </summary>
    public static MappingPolicy Default { get; } = new([1252, 65001], new ContentCodePage(1252));

    /// <summary>Indica se o code page é aceito pela política.</summary>
    public bool IsAllowed(ContentCodePage contentCodePage) =>
        allowedContentCodePages.Contains(contentCodePage.Value);

    /// <summary>Garante que o code page é aceito; caso contrário lança (fail-closed).</summary>
    public void EnsureAllowed(ContentCodePage contentCodePage)
    {
        if (!IsAllowed(contentCodePage))
        {
            throw new MappingGenerationException(
                $"ContentCodePage {contentCodePage.Value} não é permitido pela política de mapping.");
        }
    }
}
