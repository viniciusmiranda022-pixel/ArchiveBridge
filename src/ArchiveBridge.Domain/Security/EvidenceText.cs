using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Validação COMPARTILHADA de texto livre de evidência de segurança (AB-I7-008): combina
/// <see cref="TextValue.Require"/> (forma) com <see cref="SecretRedactor.ContainsSuspectedSecret"/> (guarda
/// fail-closed) — nenhum campo de texto livre novo introduzido por este work order aceita um valor com
/// aparência de segredo/token/SAS/cookie/e-mail/caminho UNC (STOP-THE-LINE: "nunca armazenar segredo/PII em
/// evidência").
/// </summary>
internal static class EvidenceText
{
    /// <summary>Exige um valor obrigatório, validado quanto à forma e sem aparência de segredo/PII.</summary>
    public static string RequireSafe(string value, string parameterName, int maxLength, Func<string, Exception> onSuspectedSecret)
    {
        var trimmed = TextValue.Require(value, parameterName, maxLength);
        if (SecretRedactor.ContainsSuspectedSecret(trimmed))
        {
            throw onSuspectedSecret(parameterName);
        }

        return trimmed;
    }

    /// <summary>Mesma validação de <see cref="RequireSafe"/>, mas o valor é opcional (vazio/whitespace vira <see cref="string.Empty"/>).</summary>
    public static string RequireSafeOptional(string? value, string parameterName, int maxLength, Func<string, Exception> onSuspectedSecret) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : RequireSafe(value, parameterName, maxLength, onSuspectedSecret);
}
