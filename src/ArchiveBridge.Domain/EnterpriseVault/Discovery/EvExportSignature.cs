using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Assinatura NORMALIZADA e observada de um cmdlet de exportação (ex.: <c>Export-EVArchive</c>),
/// modelada para que os adapters comparem o ambiente observado com os requisitos suportados — sem
/// assumir que a assinatura é a mesma em todas as versões do Enterprise Vault. A normalização ordena e
/// deduplica os nomes de parâmetros e conjuntos de parâmetros; o <see cref="SignatureHash"/> é
/// determinístico sobre a forma (nome + tipo + parâmetros + obrigatórios + conjuntos) — não sobre a
/// versão textual. Registrar a assinatura NÃO executa a exportação.
/// </summary>
public sealed record EvExportSignature(
    string CommandName,
    string ModuleSource,
    string ObservedVersion,
    string CommandType,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> MandatoryParameters,
    IReadOnlyList<string> ParameterSets,
    Sha256Hash SignatureHash,
    DateTimeOffset DiscoveredAtUtc)
{
    /// <summary>
    /// Cria uma assinatura normalizada (parâmetros/conjuntos ordenados e deduplicados) e computa o hash
    /// determinístico da forma. Um cmdlet ausente é modelado fora daqui (capacidade Unavailable) — este
    /// tipo representa uma assinatura EFETIVAMENTE observada.
    /// </summary>
    public static EvExportSignature Create(
        string commandName,
        string moduleSource,
        string observedVersion,
        string commandType,
        IEnumerable<string> parameters,
        IEnumerable<string> mandatoryParameters,
        IEnumerable<string> parameterSets,
        DateTimeOffset discoveredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        var normalizedParameters = Normalize(parameters);
        var normalizedMandatory = Normalize(mandatoryParameters);
        var normalizedSets = Normalize(parameterSets);

        var hash = DeterministicHash.Compute(
        [
            "cmd", commandName.Trim().ToUpperInvariant(),
            "type", commandType.Trim().ToUpperInvariant(),
            "params", string.Join(",", normalizedParameters),
            "mandatory", string.Join(",", normalizedMandatory),
            "sets", string.Join(",", normalizedSets),
        ]);

        return new EvExportSignature(
            commandName.Trim(),
            moduleSource?.Trim() ?? string.Empty,
            observedVersion?.Trim() ?? string.Empty,
            commandType.Trim(),
            normalizedParameters,
            normalizedMandatory,
            normalizedSets,
            hash,
            discoveredAtUtc);
    }

    /// <summary>Indica se a assinatura observada contém (por nome, case-insensitive) um parâmetro.</summary>
    public bool HasParameter(string parameterName) =>
        Parameters.Contains(parameterName?.Trim().ToUpperInvariant() ?? string.Empty, StringComparer.Ordinal);

    // Canonicalização determinística: trim, upper-invariant (evita ToLowerInvariant — CA1308), dedupe, sort ordinal.
    private static string[] Normalize(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
    }
}
