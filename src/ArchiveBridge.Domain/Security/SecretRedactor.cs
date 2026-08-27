using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Redator CENTRALIZADO de segredo/PII (runbook §32.1 — AB-I7-008 item 4), reutilizável por qualquer
/// camada que precise sanitizar texto antes de log/telemetria/evidência. Nunca é a ÚNICA defesa: os
/// dados sensíveis não deveriam alcançar este ponto "por design" (mesmo princípio de
/// <c>ReconciliationExceptionCommentText</c>) — este tipo é o backstop fail-closed. Determinístico: o
/// mesmo texto e o mesmo <c>tenantScopeId</c> sempre produzem a mesma saída redigida.
/// <para>
/// Cobre, no mínimo (STOP-THE-LINE do work order): query strings de URL, cabeçalho <c>Authorization</c>,
/// cookies, tokens SAS (<c>sig=</c>/<c>sharedaccesssignature</c>/<c>accountkey=</c>), bearer tokens,
/// endereços UPN/SMTP (substituídos por um placeholder HMAC escopado por tenant, NUNCA pelo valor bruto),
/// caminhos UNC e linhas com aparência de <c>Subject</c>/<c>Body</c>/<c>Attachment(-Name)</c>.
/// </para>
/// </summary>
public static partial class SecretRedactor
{
    private const string RedactedToken = "[REDACTED]";
    private const string RedactedUncPathToken = "[REDACTED_UNC_PATH]";

    [GeneratedRegex(@"\?[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex QueryStringPattern();

    [GeneratedRegex(@"^(authorization\s*:\s*).+$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(@"^((?:set-)?cookie\s*:\s*).+$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CookiePattern();

    [GeneratedRegex(@"\b(sig|sharedaccesssignature|accountkey)=[^\s&;""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SasTokenPattern();

    [GeneratedRegex(@"\bbearer\s+[a-z0-9\-_.=]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"\\\\[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathPattern();

    [GeneratedRegex(
        @"^((?:subject|body|attachment(?:-name)?)\s*:\s*).*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveLabeledLinePattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddressPattern();

    /// <summary>
    /// Redige TODOS os formatos conhecidos de segredo/PII de <paramref name="input"/>. Endereços de
    /// e-mail/UPN são substituídos por um placeholder HMAC-SHA256 escopado por <paramref name="tenantScopeId"/>
    /// (determinístico, não reversível, nunca o valor bruto).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tenantScopeId"/> vazio/inválido.</exception>
    public static string Redact(string input, string tenantScopeId)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var scope = TextValue.Require(tenantScopeId, nameof(tenantScopeId), 200);

        var redacted = input;
        redacted = AuthorizationHeaderPattern().Replace(redacted, $"$1{RedactedToken}");
        redacted = CookiePattern().Replace(redacted, $"$1{RedactedToken}");
        redacted = SensitiveLabeledLinePattern().Replace(redacted, $"$1{RedactedToken}");
        redacted = BearerTokenPattern().Replace(redacted, $"bearer {RedactedToken}");
        redacted = SasTokenPattern().Replace(redacted, $"$1={RedactedToken}");
        redacted = QueryStringPattern().Replace(redacted, $"?{RedactedToken}");
        redacted = UncPathPattern().Replace(redacted, RedactedUncPathToken);
        redacted = EmailAddressPattern().Replace(redacted, match => RedactEmail(match.Value, scope));
        return redacted;
    }

    /// <summary>
    /// Heurística fail-closed: verdadeiro quando <paramref name="text"/> aparenta conter algum formato de
    /// segredo/PII coberto por <see cref="Redact"/>. Usada como GUARDA DE ACEITAÇÃO (nunca redação) em
    /// campos de texto livre de evidência que nunca deveriam carregar segredo/PII "por design" — mesmo
    /// princípio de <c>ReconciliationExceptionCommentText.SuspectedSecretPattern</c>.
    /// </summary>
    public static bool ContainsSuspectedSecret(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return AuthorizationHeaderPattern().IsMatch(text)
            || CookiePattern().IsMatch(text)
            || SasTokenPattern().IsMatch(text)
            || BearerTokenPattern().IsMatch(text)
            || UncPathPattern().IsMatch(text)
            || EmailAddressPattern().IsMatch(text)
            || SensitiveLabeledLinePattern().IsMatch(text);
    }

    private static string RedactEmail(string emailAddress, string tenantScopeId)
    {
        var keyBytes = Encoding.UTF8.GetBytes(tenantScopeId);
        var valueBytes = Encoding.UTF8.GetBytes(emailAddress.ToUpperInvariant());
        var hash = HMACSHA256.HashData(keyBytes, valueBytes);
        return $"[UPN:{Convert.ToHexStringLower(hash)[..16]}]";
    }
}
