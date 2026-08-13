using System.Globalization;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Evidence;

/// <summary>
/// HISTÓRICO paginado (keyset/seek) de evidências de descoberta, com filtros bounded/parametrizados. É
/// estritamente READ-ONLY: nenhuma navegação executa ação nem gera evento de auditoria. O escopo é sempre
/// re-resolvido do principal; o cursor carrega apenas as chaves de ordenação + o fingerprint dos filtros e
/// nunca troca tenant/projeto. O download verificado de <c>evidence.json</c> permanece inalterado (link por
/// ambiente/versão para <c>/Evidence/Download</c>).
/// </summary>
public sealed class IndexModel(IControlPlaneQueries queries, IPortalScopeAccessor scopeAccessor) : PageModel
{
    private const int SitePrefixMaxLength = 100;

    private readonly IControlPlaneQueries _queries = queries;
    private readonly IPortalScopeAccessor _scopeAccessor = scopeAccessor;

    /// <summary>Itens da fatia atual do histórico.</summary>
    public IReadOnlyList<EvEvidenceListItem> Items { get; private set; } = [];

    /// <summary>Cursor da próxima fatia (quando há mais); caso contrário <see langword="null"/>.</summary>
    public string? NextCursor { get; private set; }

    /// <summary>Verdadeiro quando há mais páginas.</summary>
    public bool HasMore { get; private set; }

    /// <summary>Tamanho de página efetivo (após clamp).</summary>
    public int PageSize { get; private set; } = KeysetPaging.DefaultPageSize;

    /// <summary>Valores dos filtros para repovoar o formulário.</summary>
    public string? SiteFilter { get; private set; }

    /// <summary>Ambiente filtrado (GUID canônico).</summary>
    public string? EnvironmentFilter { get; private set; }

    /// <summary>Resultado filtrado (nome do enum).</summary>
    public string? ResultStatusFilter { get; private set; }

    /// <summary>Data inicial (texto cru submetido).</summary>
    public string? FromFilter { get; private set; }

    /// <summary>Data final (texto cru submetido).</summary>
    public string? ToFilter { get; private set; }

    /// <summary>Route data da próxima página (filtros atuais + tamanho + cursor).</summary>
    public IDictionary<string, string> NextPageRoute { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Renderiza a fatia do histórico correspondente aos filtros/cursor. 400 fail-closed em entrada inválida.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var scope = _scopeAccessor.Resolve(User);
        var query = Request.Query;

        // Site prefix — trim, tamanho máximo; wildcard NÃO é interpretado (a query escapa metacaracteres LIKE).
        string? sitePrefix = null;
        var siteRaw = query["site"].ToString();
        if (!string.IsNullOrWhiteSpace(siteRaw))
        {
            var trimmed = siteRaw.Trim();
            if (trimmed.Length > SitePrefixMaxLength)
            {
                return BadRequest();
            }

            sitePrefix = trimmed;
        }

        SiteFilter = sitePrefix;

        // Environment — GUID exato e diferente de Guid.Empty quando informado.
        Guid? environmentId = null;
        var envRaw = query["env"].ToString();
        if (!string.IsNullOrWhiteSpace(envRaw))
        {
            if (!Guid.TryParse(envRaw, out var env) || env == Guid.Empty)
            {
                return BadRequest();
            }

            environmentId = env;
            EnvironmentFilter = env.ToString("D");
        }

        // Result status — conjunto FECHADO {Ready, Blocked, Unsupported, Failed} (coluna result_status).
        EvDiscoveryStatus? resultStatus = null;
        var resultRaw = query["result"].ToString();
        if (!string.IsNullOrWhiteSpace(resultRaw))
        {
            if (!Enum.TryParse<EvDiscoveryStatus>(resultRaw, ignoreCase: true, out var parsed)
                || parsed is not (EvDiscoveryStatus.Ready or EvDiscoveryStatus.Blocked
                    or EvDiscoveryStatus.Unsupported or EvDiscoveryStatus.Failed))
            {
                return BadRequest();
            }

            resultStatus = parsed;
            ResultStatusFilter = parsed.ToString();
        }

        // Datas — UTC; From > To ⇒ 400.
        if (!TryParseUtc(query["from"].ToString(), out var fromUtc)
            || !TryParseUtc(query["to"].ToString(), out var toUtc))
        {
            return BadRequest();
        }

        if (fromUtc is { } fromValue && toUtc is { } toValue && fromValue > toValue)
        {
            return BadRequest();
        }

        FromFilter = EmptyToNull(query["from"].ToString());
        ToFilter = EmptyToNull(query["to"].ToString());

        PageSize = ParsePageSize(query["size"].ToString());

        var filter = new EvEvidenceFilter(sitePrefix, environmentId, resultStatus, fromUtc, toUtc);
        var fingerprint = FilterFingerprint.Compute(
            sitePrefix,
            environmentId?.ToString("D"),
            resultStatus?.ToString(),
            fromUtc?.ToString("O", CultureInfo.InvariantCulture),
            toUtc?.ToString("O", CultureInfo.InvariantCulture));

        // Cursor — opaco, versionado, ligado ao fingerprint dos filtros. Inválido/mismatch ⇒ 400 (nunca 500).
        EvidenceSeekPosition? after = null;
        var cursorRaw = query["cursor"].ToString();
        if (!string.IsNullOrEmpty(cursorRaw)
            && !KeysetCursor.TryDecode(cursorRaw, fingerprint, out after))
        {
            return BadRequest();
        }

        var page = await _queries.SearchEvEvidencePageAsync(scope, filter, PageSize, after, cancellationToken)
            .ConfigureAwait(false);
        Items = page.Items;
        HasMore = page.HasMore;
        if (page.HasMore && page.NextPosition is not null)
        {
            NextCursor = KeysetCursor.Encode(fingerprint, page.NextPosition);
            NextPageRoute = BuildNextRoute();
        }

        return Page();
    }

    private Dictionary<string, string> BuildNextRoute()
    {
        var route = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["size"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["cursor"] = NextCursor!,
        };
        if (SiteFilter is not null)
        {
            route["site"] = SiteFilter;
        }

        if (EnvironmentFilter is not null)
        {
            route["env"] = EnvironmentFilter;
        }

        if (ResultStatusFilter is not null)
        {
            route["result"] = ResultStatusFilter;
        }

        if (FromFilter is not null)
        {
            route["from"] = FromFilter;
        }

        if (ToFilter is not null)
        {
            route["to"] = ToFilter;
        }

        return route;
    }

    private static bool TryParseUtc(string raw, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static int ParsePageSize(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested)
            ? KeysetPaging.ClampPageSize(requested)
            : KeysetPaging.DefaultPageSize;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
