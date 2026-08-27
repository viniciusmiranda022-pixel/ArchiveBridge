using System.Buffers;
using System.Text.RegularExpressions;
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

    /// <summary>Raiz de um caminho Windows absoluto ('X:\'); qualquer outra forma (relativa, UNC, curinga) é ambígua e recusada.</summary>
    private static readonly Regex DriveRootPattern = new(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly SearchValues<char> InvalidPathSegmentChars =
        SearchValues.Create(['*', '?', '"', '<', '>', '|', ':', '/']);

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
    /// Nem hash nem (publisher + path rule específica) foram informados, ou a path rule informada não é
    /// um caminho Windows absoluto específico (vazia, curinga, relativa, ambígua, ou equivalente à raiz
    /// do drive — o que equivaleria a allow-all).
    /// </exception>
    public static WdacAllowlistEntry Create(string? publisher, Sha256Hash? sha256, string? pathRule)
    {
        var sanitizedPublisher = string.IsNullOrWhiteSpace(publisher)
            ? null
            : TextValue.Require(publisher, nameof(publisher), PublisherMaxLength);
        var rawPathRule = string.IsNullOrWhiteSpace(pathRule)
            ? null
            : TextValue.Require(pathRule, nameof(pathRule), PathRuleMaxLength);
        var sanitizedPathRule = rawPathRule is null ? null : CanonicalizePathRule(rawPathRule);

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
            // AB-I7-011: o candidato é entrada NÃO CONFIÁVEL — precisa ser canonicalizado com a MESMA
            // rotina Windows-aware usada para a path rule antes de qualquer comparação. Um candidato que
            // não canonicaliza de forma inequívoca (relativo, '.'/'..', '/', UNC, ADS/curinga, etc.) é
            // recusado/Denied — nunca normalizado de forma permissiva.
            && TryCanonicalizeWindowsPath(candidate.Path, out var canonicalCandidatePath)
            && MatchesPathRuleBoundary(PathRule, canonicalCandidatePath);
    }

    /// <summary>
    /// Canonicaliza uma path rule Windows: exige raiz de drive absoluta ('X:\...'), rejeita separadores
    /// '/' (ambíguos), segmentos relativos ('.'/'..') ou caracteres reservados, e remove separador(es)
    /// finais — para que o matching por boundary (<see cref="MatchesPathRuleBoundary"/>) nunca degenere
    /// em um mero prefixo lexical (ex.: 'Worker' aceitando 'WorkerEvil').
    /// </summary>
    private static string CanonicalizePathRule(string pathRule)
    {
        if (!TryCanonicalizeWindowsPath(pathRule, out var canonicalPathRule))
        {
            throw new WdacPolicyInvariantViolationException(
                "Path rule precisa ser um caminho Windows absoluto, específico e sem ambiguidade — sem segmentos " +
                "relativos ('.'/'..'), sem separadores '/' alternativos, sem UNC, sem caractere reservado " +
                "(':', '*', '?', '\"', '<', '>', '|') fora do drive designator, e não apontando apenas para a " +
                "raiz do drive (o que equivaleria a allow-all) — recusada por design (fail-closed).");
        }

        return canonicalPathRule;
    }

    /// <summary>
    /// Tenta canonicalizar um caminho Windows de forma determinística e inequívoca: raiz de drive absoluta
    /// obrigatória ('X:\...'), nenhum segmento relativo ('.'/'..'), nenhum separador alternativo ('/'),
    /// nenhum caractere reservado/ambíguo ('*', '?', '"', '&lt;', '&gt;', '|', ':' — este último cobre também
    /// alternate-data-stream markers) e não apenas a raiz do drive. Retorna falso — sem lançar — para
    /// qualquer forma que não permita provar boundary de contenção com segurança; usada tanto para validar a
    /// <see cref="PathRule"/> declarada (fail-closed na criação, AB-I7-010) quanto para o
    /// <see cref="WdacCandidateBinary.Path"/> apresentado a validação (fail-closed em Denied, AB-I7-011) —
    /// a MESMA rotina para ambos, para que nenhum dos dois lados possa escapar do boundary por uma forma que
    /// o outro lado recusaria.
    /// </summary>
    private static bool TryCanonicalizeWindowsPath(string path, out string canonicalPath)
    {
        canonicalPath = string.Empty;

        var driveRoot = DriveRootPattern.Match(path);
        if (!driveRoot.Success)
        {
            return false;
        }

        var segments = path[driveRoot.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            // Apontar apenas para a raiz do drive (ou apenas separadores redundantes) não prova nenhum
            // boundary de contenção específico — equivaleria a allow-all para uma path rule, e não pode ser
            // resolvido com segurança para um candidato.
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(InvalidPathSegmentChars) >= 0)
            {
                return false;
            }
        }

        canonicalPath = path[..2] + '\\' + string.Join('\\', segments);
        return true;
    }

    /// <summary>
    /// Verdadeiro quando <paramref name="canonicalCandidatePath"/> é EXATAMENTE <paramref name="pathRule"/>,
    /// ou um descendente real dele (separador de path real na fronteira) — nunca um mero prefixo lexical
    /// (ex.: a regra 'C:\Worker' NUNCA corresponde a 'C:\WorkerEvil\payload.exe'). Ambos os lados já chegam
    /// aqui canonicalizados por <see cref="TryCanonicalizeWindowsPath"/>. Não segue symlink/reparse point
    /// semantics (modelo em memória) — apenas evita uma falsa garantia de contenção.
    /// </summary>
    private static bool MatchesPathRuleBoundary(string pathRule, string canonicalCandidatePath) =>
        string.Equals(canonicalCandidatePath, pathRule, StringComparison.OrdinalIgnoreCase)
            || canonicalCandidatePath.StartsWith(pathRule + '\\', StringComparison.OrdinalIgnoreCase);
}
