using System.Text.RegularExpressions;

namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Identidade ESTÁVEL de um controle do Production Readiness Review (AB-I8-001, escopo obrigatório item
/// 1: "cada controle deve possuir identidade estável"). Formato canônico fixo
/// <c>GROUP.CONTROL_NAME</c> (maiúsculas, dígitos, underscore e ponto) — nunca texto livre, para que o
/// mesmo identificador sobreviva a reformulações de texto descritivo sem quebrar histórico/joins.
/// </summary>
public readonly partial record struct ReadinessControlId
{
    private const int MaxLength = 80;

    [GeneratedRegex(@"^[A-Z][A-Z0-9]*(?:[._][A-Z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPattern();

    /// <summary>Cria um identificador canônico, validando forma (fail-closed).</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> vazio, longo demais, ou fora do formato canônico.</exception>
    public ReadinessControlId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("ReadinessControlId é obrigatório.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"ReadinessControlId excede {MaxLength} caracteres.", nameof(value));
        }

        if (!CanonicalPattern().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "ReadinessControlId deve seguir o formato canônico GROUP.CONTROL_NAME (maiúsculas/dígitos/'.'/'_').",
                nameof(value));
        }

        Value = trimmed;
    }

    /// <summary>Valor canônico do identificador.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
