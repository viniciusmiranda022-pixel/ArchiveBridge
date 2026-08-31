using System.Text.RegularExpressions;

namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Identidade ESTÁVEL de UM critério de encerramento de migração do runbook §49 (AB-I8-010, escopo obrigatório
/// item 7). Formato canônico fixo <c>COMPLETION.CRITERION_NAME</c> (maiúsculas, dígitos, underscore e ponto) —
/// nunca texto livre (mesmo padrão de <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlId"/>).
/// </summary>
public readonly partial record struct MigrationCompletionCriterionId
{
    private const int MaxLength = 80;

    [GeneratedRegex(@"^[A-Z][A-Z0-9]*(?:[._][A-Z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPattern();

    /// <summary>Cria um identificador canônico, validando forma (fail-closed).</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> vazio, longo demais, ou fora do formato canônico.</exception>
    public MigrationCompletionCriterionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("MigrationCompletionCriterionId é obrigatório.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"MigrationCompletionCriterionId excede {MaxLength} caracteres.", nameof(value));
        }

        if (!CanonicalPattern().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "MigrationCompletionCriterionId deve seguir o formato canônico COMPLETION.CRITERION_NAME.", nameof(value));
        }

        Value = trimmed;
    }

    /// <summary>Valor canônico do identificador.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
