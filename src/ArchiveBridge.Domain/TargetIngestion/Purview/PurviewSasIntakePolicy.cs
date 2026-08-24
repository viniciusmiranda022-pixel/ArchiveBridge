using System.Globalization;
using System.Text;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Validação PURA, fail-closed, da URL SAS do Purview Network Upload ANTES de qualquer custódia (runbook
/// §25.5, work order AB-I5-004 item 4). Sem I/O, sem chamada ao secret store, sem log — a única
/// responsabilidade é decidir aceitar/rejeitar e, quando aceito, extrair METADADOS NÃO SECRETOS
/// canonicalizados (item 5). O valor bruto passa pela validação em memória (é a própria natureza de um
/// "formulário secreto" que recebe o segredo) mas NUNCA é logado, incluído em exception, retornado em
/// texto livre ou reconstruído a partir de componentes — o <see cref="RedactedSecret"/> devolvido em
/// <see cref="PurviewSasValidationResult.Secret"/> preserva a string ORIGINAL exata (percent-encoding
/// incluído), porque re-serializar a partir de componentes parseados poderia invalidar a assinatura SAS
/// que o AzCopy de um Passo futuro vai consumir.
/// <para>
/// O host autorizado é validado contra o domínio de storage documentado pela Microsoft para o staging do
/// Purview Network Upload (<see cref="AuthorizedHostSuffix"/>) — comparação de SUFIXO sobre
/// <see cref="Uri.Host"/> já parseado pelo BCL (nunca <c>Contains</c> heurístico sobre a string bruta,
/// que aceitaria hosts arbitrários com o domínio embutido em outra posição). O container é validado como
/// EXATAMENTE <c>ingestiondata</c> (runbook §25.5/§25.7), case-sensitive.
/// </para>
/// </summary>
public static class PurviewSasIntakePolicy
{
    /// <summary>Sufixo de domínio do Azure Storage usado pelo staging do Purview Network Upload.</summary>
    public const string AuthorizedHostSuffix = ".blob.core.windows.net";

    /// <summary>Container de staging documentado pelo runbook §25.5/§25.7 — nunca outro.</summary>
    public const string AuthorizedContainer = "ingestiondata";

    /// <summary>Margem mínima de validade futura exigida no momento da custódia (política própria do produto).</summary>
    public static readonly TimeSpan MinimumValidityRemaining = TimeSpan.FromMinutes(5);

    /// <summary>Janela máxima de validade aceita a partir de agora (política própria do produto, defesa em profundidade).</summary>
    public static readonly TimeSpan MaximumValidityWindow = TimeSpan.FromHours(24);

    private static readonly HashSet<string> CriticalParameterNames =
        new(StringComparer.OrdinalIgnoreCase) { "sv", "se", "sp", "sig", "si", "spr" };

    /// <summary>Valida a URL SAS bruta. Nunca lança para entrada inválida — sempre devolve um resultado estruturado.</summary>
    /// <exception cref="ArgumentException"><paramref name="rawSasUri"/> é nulo/vazio (erro de chamador, não de conteúdo do SAS).</exception>
    public static PurviewSasValidationResult Validate(string rawSasUri, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(rawSasUri))
        {
            throw new ArgumentException("A URL SAS é obrigatória.", nameof(rawSasUri));
        }

        if (!Uri.TryCreate(rawSasUri, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString())
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.MalformedUri);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.SchemeNotHttps);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.UserInfoPresent);
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.FragmentPresent);
        }

        if (uri.Host.Length <= AuthorizedHostSuffix.Length
            || !uri.Host.EndsWith(AuthorizedHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.HostNotAuthorized);
        }

        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length == 0)
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.ContainerNotAuthorized);
        }

        if (pathSegments.Length > 1)
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.UnexpectedPath);
        }

        if (!string.Equals(pathSegments[0], AuthorizedContainer, StringComparison.Ordinal))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.ContainerNotAuthorized);
        }

        if (!TryParseQuery(uri.Query, out var parameters))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.DuplicateCriticalParameter);
        }

        if (parameters.ContainsKey("si"))
        {
            // SAS por policy nomeada: permissões/expiry vivem no lado do serviço, não são verificáveis
            // estaticamente aqui — recusado fail-closed (nunca presumido "provavelmente ok").
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.StoredPolicyReferenceNotVerifiable);
        }

        if (!parameters.TryGetValue("sv", out var signedVersion) || string.IsNullOrEmpty(signedVersion)
            || !parameters.TryGetValue("sig", out var signature) || string.IsNullOrEmpty(signature))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.MissingCriticalParameter);
        }

        if (!parameters.TryGetValue("se", out var expiryRaw) || string.IsNullOrEmpty(expiryRaw))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.MissingCriticalParameter);
        }

        if (!DateTimeOffset.TryParse(
                expiryRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAtUtc))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.ExpiryMalformed);
        }

        if (expiresAtUtc <= nowUtc + MinimumValidityRemaining)
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.ExpiryAlreadyExpiredOrTooSoon);
        }

        if (expiresAtUtc > nowUtc + MaximumValidityWindow)
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.ExpiryExceedsMaximumWindow);
        }

        if (parameters.TryGetValue("spr", out var protocolRestriction)
            && !string.Equals(protocolRestriction, "https", StringComparison.OrdinalIgnoreCase))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.ProtocolRestrictionNotHttpsOnly);
        }

        if (!parameters.TryGetValue("sp", out var permissionsRaw) || string.IsNullOrEmpty(permissionsRaw))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.MissingCriticalParameter);
        }

        if (!TryParsePermissions(permissionsRaw, out var permissions))
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.PermissionsUnrecognized);
        }

        if (!permissions.SatisfiesUploadPolicy())
        {
            return PurviewSasValidationResult.Reject(PurviewSasRejectionReason.PermissionsNotWithinUploadPolicy);
        }

        var fingerprint = DeterministicHash.ComputeBytes(Encoding.UTF8.GetBytes(rawSasUri));
        return PurviewSasValidationResult.Accept(
            uri.Host, pathSegments[0], expiresAtUtc, permissions, fingerprint, RedactedSecret.Wrap(rawSasUri));
    }

    /// <summary>
    /// Parseia a query string SEM usar <c>HttpUtility</c>/<c>QueryHelpers</c> (indisponíveis/indesejados
    /// em Domain) e SEM colapsar silenciosamente chaves duplicadas — uma chave crítica duplicada é
    /// tratada como ambígua e recusa o parsing inteiro (item 4: "parâmetros críticos... duplicados/ambíguos").
    /// </summary>
    private static bool TryParseQuery(string query, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        if (trimmed.Length == 0)
        {
            return true;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var rawKey = separatorIndex < 0 ? pair : pair[..separatorIndex];
            var rawValue = separatorIndex < 0 ? string.Empty : pair[(separatorIndex + 1)..];
            var key = Uri.UnescapeDataString(rawKey);

            if (CriticalParameterNames.Contains(key) && parameters.ContainsKey(key))
            {
                return false;
            }

            parameters[key] = Uri.UnescapeDataString(rawValue);
        }

        return true;
    }

    /// <summary>
    /// Decodifica o parâmetro <c>sp</c> letra a letra (mapeamento oficial de permissões de container do
    /// Azure Storage). Qualquer letra não reconhecida recusa o parsing inteiro — nunca ignorada.
    /// </summary>
    private static bool TryParsePermissions(string raw, out PurviewSasPermissions permissions)
    {
        bool read = false, add = false, create = false, write = false, delete = false, deleteVersion = false,
            permanentDelete = false, list = false, tags = false, move = false, execute = false, ownership = false,
            setPermissions = false, setImmutabilityPolicy = false;

        foreach (var character in raw)
        {
            switch (character)
            {
                case 'r': read = true; break;
                case 'a': add = true; break;
                case 'c': create = true; break;
                case 'w': write = true; break;
                case 'd': delete = true; break;
                case 'x': deleteVersion = true; break;
                case 'y': permanentDelete = true; break;
                case 'l': list = true; break;
                case 't': tags = true; break;
                case 'm': move = true; break;
                case 'e': execute = true; break;
                case 'o': ownership = true; break;
                case 'p': setPermissions = true; break;
                case 'i': setImmutabilityPolicy = true; break;
                default:
                    permissions = null!;
                    return false;
            }
        }

        permissions = new PurviewSasPermissions(
            read, add, create, write, delete, deleteVersion, permanentDelete, list, tags, move, execute, ownership,
            setPermissions, setImmutabilityPolicy);
        return true;
    }
}
