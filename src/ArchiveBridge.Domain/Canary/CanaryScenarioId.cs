using System.Text.RegularExpressions;

namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Identidade ESTÁVEL de um cenário obrigatório do canário de produção (AB-I8-004, escopo obrigatório item 3:
/// "materializar, com identidade estável e status explícito, os cenários documentados" do runbook §48).
/// Formato canônico fixo <c>CANARY.SCENARIO_NAME</c> (maiúsculas, dígitos, underscore e ponto) — nunca texto
/// livre, mesmo princípio de <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlId"/>.
/// </summary>
public readonly partial record struct CanaryScenarioId
{
    private const int MaxLength = 80;

    [GeneratedRegex(@"^[A-Z][A-Z0-9]*(?:[._][A-Z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPattern();

    /// <summary>Cria um identificador canônico, validando forma (fail-closed).</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> vazio, longo demais, ou fora do formato canônico.</exception>
    public CanaryScenarioId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("CanaryScenarioId é obrigatório.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"CanaryScenarioId excede {MaxLength} caracteres.", nameof(value));
        }

        if (!CanonicalPattern().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "CanaryScenarioId deve seguir o formato canônico GROUP.SCENARIO_NAME (maiúsculas/dígitos/'.'/'_').",
                nameof(value));
        }

        Value = trimmed;
    }

    /// <summary>Valor canônico do identificador.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
