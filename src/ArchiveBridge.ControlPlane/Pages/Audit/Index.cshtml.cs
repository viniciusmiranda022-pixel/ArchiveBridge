using System.Globalization;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.ControlPlane.Composition;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Audit;

/// <summary>
/// Trilha de auditoria paginada (keyset/seek), restrita a Auditor/Administrator. Carrega DUAS listas
/// INDEPENDENTES — eventos operacionais (tenant + projeto, sob RLS) e autenticação (tenant-wide, filtro
/// explícito por tenant) — cada uma com seus próprios filtros e cursor. Avançar uma lista preserva o estado
/// da outra. É estritamente READ-ONLY: visualizar/filtrar/paginar NÃO gera nenhum evento de auditoria.
/// </summary>
public sealed class IndexModel(
    IPortalOperationalAudit operationalAudit,
    IPortalSignInAudit signInAudit,
    IPortalScopeAccessor scopeAccessor) : PageModel
{
    private const int ActionCodeMaxLength = 64;
    private const int UsernameMaxLength = 200;
    private const int ReasonMaxLength = 100;

    private readonly IPortalOperationalAudit _operationalAudit = operationalAudit;
    private readonly IPortalSignInAudit _signInAudit = signInAudit;
    private readonly IPortalScopeAccessor _scopeAccessor = scopeAccessor;

    // ---- Operacional ----
    public IReadOnlyList<PortalOperationalAuditEvent> Operations { get; private set; } = [];
    public bool OperationsHasMore { get; private set; }
    public IDictionary<string, string> OperationsNextRoute { get; private set; } = Empty();
    public string? OpAction { get; private set; }
    public string? OpUsername { get; private set; }
    public string? OpSucceeded { get; private set; }
    public string? OpCorrelation { get; private set; }
    public string? OpFrom { get; private set; }
    public string? OpTo { get; private set; }
    public string? OpCursor { get; private set; }
    public int OpPageSize { get; private set; } = KeysetPaging.DefaultPageSize;

    // ---- Autenticação ----
    public IReadOnlyList<PortalSignInEvent> SignIns { get; private set; } = [];
    public bool SignInsHasMore { get; private set; }
    public IDictionary<string, string> SignInsNextRoute { get; private set; } = Empty();
    public string? AuthUsername { get; private set; }
    public string? AuthSucceeded { get; private set; }
    public string? AuthReason { get; private set; }
    public string? AuthFrom { get; private set; }
    public string? AuthTo { get; private set; }
    public string? AuthCursor { get; private set; }
    public int AuthPageSize { get; private set; } = KeysetPaging.DefaultPageSize;

    /// <summary>Carrega as duas trilhas independentes. 400 fail-closed em qualquer entrada inválida.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var scope = _scopeAccessor.Resolve(User);
        var query = Request.Query;

        // ---------- Operacional ----------
        if (!TryBoundedExact(query["opAction"], ActionCodeMaxLength, out var opAction)
            || !TryBoundedExact(query["opUsername"], UsernameMaxLength, out var opUsername)
            || !TryTriState(query["opSucceeded"], out var opSucceeded)
            || !TryGuidExact(query["opCorrelation"], out var opCorrelation)
            || !TryUtc(query["opFrom"], out var opFrom)
            || !TryUtc(query["opTo"], out var opTo))
        {
            return BadRequest();
        }

        if (opFrom is { } opFromValue && opTo is { } opToValue && opFromValue > opToValue)
        {
            return BadRequest();
        }

        OpAction = opAction;
        OpUsername = opUsername;
        OpSucceeded = opSucceeded?.ToString().ToLowerInvariant();
        OpCorrelation = opCorrelation?.ToString("D");
        OpFrom = opFrom is null ? null : EmptyToNull(query["opFrom"]);
        OpTo = opTo is null ? null : EmptyToNull(query["opTo"]);
        OpCursor = EmptyToNull(query["opCursor"]);
        OpPageSize = ParsePageSize(query["opSize"]);

        var opFingerprint = FilterFingerprint.Compute(
            opAction, opUsername, opSucceeded?.ToString(), opCorrelation?.ToString("D"),
            opFrom?.ToString("O", CultureInfo.InvariantCulture), opTo?.ToString("O", CultureInfo.InvariantCulture));

        var opCursorRaw = query["opCursor"].ToString();
        AuditSeekPosition? opAfter = null;
        if (!string.IsNullOrEmpty(opCursorRaw) && !KeysetCursor.TryDecode(opCursorRaw, opFingerprint, out opAfter))
        {
            return BadRequest();
        }

        // ---------- Autenticação ----------
        if (!TryBoundedExact(query["authUsername"], UsernameMaxLength, out var authUsername)
            || !TryTriState(query["authSucceeded"], out var authSucceeded)
            || !TryBoundedExact(query["authReason"], ReasonMaxLength, out var authReason)
            || !TryUtc(query["authFrom"], out var authFrom)
            || !TryUtc(query["authTo"], out var authTo))
        {
            return BadRequest();
        }

        if (authFrom is { } authFromValue && authTo is { } authToValue && authFromValue > authToValue)
        {
            return BadRequest();
        }

        AuthUsername = authUsername;
        AuthSucceeded = authSucceeded?.ToString().ToLowerInvariant();
        AuthReason = authReason;
        AuthFrom = authFrom is null ? null : EmptyToNull(query["authFrom"]);
        AuthTo = authTo is null ? null : EmptyToNull(query["authTo"]);
        AuthCursor = EmptyToNull(query["authCursor"]);
        AuthPageSize = ParsePageSize(query["authSize"]);

        var authFingerprint = FilterFingerprint.Compute(
            authUsername, authSucceeded?.ToString(), authReason,
            authFrom?.ToString("O", CultureInfo.InvariantCulture), authTo?.ToString("O", CultureInfo.InvariantCulture));

        var authCursorRaw = query["authCursor"].ToString();
        AuditSeekPosition? authAfter = null;
        if (!string.IsNullOrEmpty(authCursorRaw) && !KeysetCursor.TryDecode(authCursorRaw, authFingerprint, out authAfter))
        {
            return BadRequest();
        }

        // ---------- Consultas independentes ----------
        var opPage = await _operationalAudit.SearchAsync(
            scope,
            new PortalOperationalAuditFilter(opAction, opUsername, opSucceeded, opCorrelation, opFrom, opTo),
            OpPageSize, opAfter, cancellationToken).ConfigureAwait(false);
        Operations = opPage.Items;
        OperationsHasMore = opPage.HasMore;

        var authPage = await _signInAudit.SearchAsync(
            scope.Tenant,
            new PortalSignInAuditFilter(authUsername, authSucceeded, authReason, authFrom, authTo),
            AuthPageSize, authAfter, cancellationToken).ConfigureAwait(false);
        SignIns = authPage.Items;
        SignInsHasMore = authPage.HasMore;

        // ---------- Rotas de "Próxima" (uma lista avança; a outra é preservada) ----------
        var opParams = OperationalParams(opCursorRaw);
        var authParams = SignInParams(authCursorRaw);

        if (opPage.HasMore && opPage.NextPosition is not null)
        {
            var next = OperationalParams(KeysetCursor.Encode(opFingerprint, opPage.NextPosition));
            OperationsNextRoute = Merge(next, authParams);
        }

        if (authPage.HasMore && authPage.NextPosition is not null)
        {
            var next = SignInParams(KeysetCursor.Encode(authFingerprint, authPage.NextPosition));
            SignInsNextRoute = Merge(opParams, next);
        }

        return Page();
    }

    // Parâmetros de query da seção OPERACIONAL (filtros + tamanho + o cursor informado, para preservação).
    private Dictionary<string, string> OperationalParams(string? cursor)
    {
        var route = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["opSize"] = OpPageSize.ToString(CultureInfo.InvariantCulture),
        };
        AddIfPresent(route, "opAction", OpAction);
        AddIfPresent(route, "opUsername", OpUsername);
        AddIfPresent(route, "opSucceeded", OpSucceeded);
        AddIfPresent(route, "opCorrelation", OpCorrelation);
        AddIfPresent(route, "opFrom", OpFrom);
        AddIfPresent(route, "opTo", OpTo);
        AddIfPresent(route, "opCursor", string.IsNullOrEmpty(cursor) ? null : cursor);
        return route;
    }

    // Parâmetros de query da seção AUTENTICAÇÃO.
    private Dictionary<string, string> SignInParams(string? cursor)
    {
        var route = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["authSize"] = AuthPageSize.ToString(CultureInfo.InvariantCulture),
        };
        AddIfPresent(route, "authUsername", AuthUsername);
        AddIfPresent(route, "authSucceeded", AuthSucceeded);
        AddIfPresent(route, "authReason", AuthReason);
        AddIfPresent(route, "authFrom", AuthFrom);
        AddIfPresent(route, "authTo", AuthTo);
        AddIfPresent(route, "authCursor", string.IsNullOrEmpty(cursor) ? null : cursor);
        return route;
    }

    private static Dictionary<string, string> Merge(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var merged = new Dictionary<string, string>(a, StringComparer.Ordinal);
        foreach (var (key, value) in b)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static void AddIfPresent(Dictionary<string, string> route, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            route[key] = value;
        }
    }

    private static bool TryBoundedExact(string? raw, int maxLength, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > maxLength)
        {
            return false;
        }

        value = trimmed;
        return true;
    }

    private static bool TryTriState(string? raw, out bool? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGuidExact(string? raw, out Guid? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!Guid.TryParse(raw, out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryUtc(string? raw, out DateTimeOffset? value)
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

    private static int ParsePageSize(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested)
            ? KeysetPaging.ClampPageSize(requested)
            : KeysetPaging.DefaultPageSize;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);
}
