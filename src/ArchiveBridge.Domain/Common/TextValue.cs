namespace ArchiveBridge.Domain.Common;

/// <summary>Validação comum de valores textuais de domínio (fail-closed).</summary>
internal static class TextValue
{
    /// <summary>Exige texto não vazio, sem caracteres de controle e dentro do tamanho máximo.</summary>
    public static string Require(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} é obrigatório.", parameterName);
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} excede {maxLength} caracteres.", parameterName);
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException($"{parameterName} contém caractere de controle.", parameterName);
            }
        }

        return trimmed;
    }
}
