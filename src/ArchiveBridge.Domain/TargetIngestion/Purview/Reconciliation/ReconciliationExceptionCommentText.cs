using System.Text.RegularExpressions;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Sanitização/validação fail-closed do comentário livre opcional de uma decisão de disposition (item 16 do
/// work order AB-I6-010): tamanho limitado, caracteres controlados (sem caracteres de controle, nunca HTML
/// bruto — output encoding é responsabilidade EXCLUSIVA da camada de apresentação, nunca deste texto) e uma
/// defesa heurística contra segredos/tokens/SAS coladas por engano (nunca é a única defesa — o comentário
/// nunca deve conter segredos "por design"; um operador que precisa referenciar evidência sensível referencia
/// o identificador canônico já auditável, nunca o segredo em si).
/// </summary>
internal static partial class ReconciliationExceptionCommentText
{
    private const int MaxLength = 500;

    [GeneratedRegex(
        "sharedaccesssignature|accountkey=|sig=|bearer\\s|password=|pwd=|-----begin |eyj[a-z0-9_-]{10,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuspectedSecretPattern();

    /// <summary>
    /// Sanitiza o comentário: <see langword="null"/>/vazio permanece <see langword="null"/> (comentário é
    /// opcional). Um comentário informado é aparado, validado contra caracteres de controle e tamanho máximo,
    /// e recusado fail-closed se aparentar conter um segredo/token/SAS colado por engano.
    /// </summary>
    /// <exception cref="ReconciliationExceptionDispositionValidationException">
    /// O comentário excede <see cref="MaxLength"/> caracteres, contém caractere de controle, ou aparenta
    /// conter um segredo/token/SAS.
    /// </exception>
    public static string? Sanitize(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        var trimmed = comment.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ReconciliationExceptionDispositionValidationException(
                $"O comentário da decisão excede o limite de {MaxLength} caracteres (fail-closed).");
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                throw new ReconciliationExceptionDispositionValidationException(
                    "O comentário da decisão contém um caractere de controle não permitido (fail-closed).");
            }
        }

        if (SuspectedSecretPattern().IsMatch(trimmed))
        {
            throw new ReconciliationExceptionDispositionValidationException(
                "O comentário da decisão aparenta conter um segredo/token/SAS — recusado por design (fail-closed).");
        }

        return trimmed;
    }
}
