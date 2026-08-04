namespace ArchiveBridge.Domain.Waves;

/// <summary>
/// Pasta raiz de destino da onda, no formato canônico <c>/ImportedPst_&lt;Project&gt;_&lt;Wave&gt;</c>.
/// Value object com validação e canonicalização determinística (fail-closed): começa com '/', nunca
/// é apenas '/', sem '..', sem barra invertida, sem caracteres inseguros e com tamanho limitado.
/// Após a aprovação da onda a pasta é congelada; um retry preserva exatamente o mesmo valor.
/// </summary>
public readonly record struct TargetRootFolder
{
    private const int MaxLength = 400;

    /// <summary>Cria e canonicaliza uma pasta de destino.</summary>
    public TargetRootFolder(string value) => Value = Canonicalize(value);

    /// <summary>Caminho canônico da pasta.</summary>
    public string Value { get; }

    /// <summary>Constrói a pasta canônica para um projeto/onda a partir de segmentos seguros.</summary>
    public static TargetRootFolder ForWave(string projectSegment, string waveSegment) =>
        new($"/ImportedPst_{RequireSegment(projectSegment, nameof(projectSegment))}_{RequireSegment(waveSegment, nameof(waveSegment))}");

    private static string Canonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("TargetRootFolder é obrigatório.", nameof(value));
        }

        var path = value.Trim();
        if (path.Length > MaxLength)
        {
            throw new ArgumentException($"TargetRootFolder excede {MaxLength} caracteres.", nameof(value));
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("TargetRootFolder não pode conter barra invertida.", nameof(value));
        }

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("TargetRootFolder deve começar com '/'.", nameof(value));
        }

        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("TargetRootFolder não pode conter '..'.", nameof(value));
        }

        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }

        if (path.Length > 1 && path.EndsWith('/'))
        {
            path = path[..^1];
        }

        if (path == "/")
        {
            throw new ArgumentException("TargetRootFolder não pode ser apenas '/'.", nameof(value));
        }

        foreach (var character in path)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '/'))
            {
                throw new ArgumentException(
                    $"TargetRootFolder contém caractere inseguro: '{character}'.", nameof(value));
            }
        }

        return path;
    }

    private static string RequireSegment(string? segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException($"{parameterName} é obrigatório.", parameterName);
        }

        var normalized = segment.Trim();
        foreach (var character in normalized)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            {
                throw new ArgumentException(
                    $"{parameterName} contém caractere inseguro: '{character}'.", parameterName);
            }
        }

        return normalized;
    }
}
