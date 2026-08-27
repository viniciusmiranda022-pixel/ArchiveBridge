using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// UMA entrada da allowlist WDAC/App Control (AB-I7-008 item 2) — identificada por hash SHA-256 exato
/// e/ou por publisher combinado com uma path rule ESPECÍFICA (não curinga). Rejeita, por construção,
/// qualquer combinação que equivaleria a allow-all (ex.: path rule vazia/curinga sem hash/publisher) —
/// nenhuma entrada "abre tudo".
/// </summary>
public sealed record WdacAllowlistEntry
{
    private const int PublisherMaxLength = 300;
    private const int PathRuleMaxLength = 500;

    private WdacAllowlistEntry(string? publisher, Sha256Hash? sha256, string? pathRule)
    {
        Publisher = publisher;
        Sha256 = sha256;
        PathRule = pathRule;
    }

    /// <summary>Publisher declarado do binário (assinatura de código), quando conhecido.</summary>
    public string? Publisher { get; }

    /// <summary>Hash SHA-256 exato do binário aprovado, quando a identificação é por hash.</summary>
    public Sha256Hash? Sha256 { get; }

    /// <summary>Path rule ESPECÍFICA (nunca curinga/raiz) que escopa onde o publisher é aceito.</summary>
    public string? PathRule { get; }

    /// <summary>
    /// Cria uma entrada válida da allowlist.
    /// </summary>
    /// <exception cref="WdacPolicyInvariantViolationException">
    /// Nem hash nem (publisher + path rule específica) foram informados, ou a path rule informada
    /// equivaleria a allow-all (vazia, ou composta somente por curingas/separadores).
    /// </exception>
    public static WdacAllowlistEntry Create(string? publisher, Sha256Hash? sha256, string? pathRule)
    {
        var sanitizedPublisher = string.IsNullOrWhiteSpace(publisher)
            ? null
            : TextValue.Require(publisher, nameof(publisher), PublisherMaxLength);
        var sanitizedPathRule = string.IsNullOrWhiteSpace(pathRule)
            ? null
            : TextValue.Require(pathRule, nameof(pathRule), PathRuleMaxLength);

        if (sanitizedPathRule is not null && sanitizedPathRule.All(static c => c is '*' or '?' or '\\' or '/'))
        {
            throw new WdacPolicyInvariantViolationException(
                "Uma path rule composta somente por curingas/separadores equivaleria a allow-all — recusada por design (fail-closed).");
        }

        var hasHash = sha256 is { Value.Length: > 0 };
        var hasScopedPath = sanitizedPublisher is not null && sanitizedPathRule is not null;

        if (!hasHash && !hasScopedPath)
        {
            throw new WdacPolicyInvariantViolationException(
                "Uma entrada da allowlist precisa ser identificada por hash SHA-256 exato, ou por publisher combinado " +
                "com uma path rule específica (não curinga) — entradas allow-all são recusadas por design.");
        }

        return new WdacAllowlistEntry(sanitizedPublisher, sha256, sanitizedPathRule);
    }

    /// <summary>Verdadeiro quando <paramref name="candidate"/> corresponde a esta entrada.</summary>
    public bool Matches(WdacCandidateBinary candidate)
    {
        if (Sha256 is { } hash)
        {
            return candidate.Sha256 is { } candidateHash
                && string.Equals(hash.Value, candidateHash.Value, StringComparison.Ordinal);
        }

        return Publisher is not null && PathRule is not null
            && !string.IsNullOrEmpty(candidate.Publisher)
            && !string.IsNullOrEmpty(candidate.Path)
            && string.Equals(Publisher, candidate.Publisher, StringComparison.OrdinalIgnoreCase)
            && candidate.Path.StartsWith(PathRule, StringComparison.OrdinalIgnoreCase);
    }
}
