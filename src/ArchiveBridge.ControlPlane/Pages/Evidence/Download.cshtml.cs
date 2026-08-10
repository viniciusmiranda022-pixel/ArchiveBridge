using System.Security.Claims;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Evidence;

/// <summary>
/// Download autenticado de <c>evidence.json</c>. O SQL só resolve a âncora no tenant/projeto do usuário;
/// o store do Slice 3 valida containment, conjunto completo, sidecar SHA-256 e manifesto ANTES de devolver
/// os bytes. O handler ainda reconcilia hash/tamanho/caminho contra o SQL e só então entrega os bytes exatos.
/// Qualquer inconsistência falha fechada e é auditada sem expor caminho físico ou detalhe interno.
/// </summary>
public sealed class DownloadModel(
    IControlPlaneQueries queries,
    IEvDiscoveryEvidenceStore evidenceStore,
    IPortalOperationalAudit operationalAudit,
    IPortalScopeAccessor scopeAccessor,
    IClock clock,
    ILogger<DownloadModel> logger) : PageModel
{
    private const string ActionCode = "evidence.download";
    private const string ResourceType = "ev-discovery-evidence";

    private readonly IControlPlaneQueries _queries = queries;
    private readonly IEvDiscoveryEvidenceStore _evidenceStore = evidenceStore;
    private readonly IPortalOperationalAudit _operationalAudit = operationalAudit;
    private readonly IPortalScopeAccessor _scopeAccessor = scopeAccessor;
    private readonly IClock _clock = clock;
    private readonly ILogger<DownloadModel> _logger = logger;

    /// <summary>Executa o download verificado.</summary>
    public async Task<IActionResult> OnGetAsync(
        Guid environmentId,
        int version,
        CancellationToken cancellationToken)
    {
        var scope = _scopeAccessor.Resolve(User);
        var correlationId = Guid.NewGuid();
        var resourceId = $"{environmentId:N}/v{version}";
        var username = User.Identity?.Name ?? string.Empty;

        if (!Guid.TryParse(User.FindFirstValue(PortalClaims.UserId), out var userId))
        {
            _logger.LogWarning("Evidence download denied: missing user claim. CorrelationId={CorrelationId}", correlationId);
            return Forbid();
        }

        if (environmentId == Guid.Empty || version <= 0)
        {
            await AuditAsync(scope, userId, username, resourceId, false, "invalid-request", correlationId, cancellationToken)
                .ConfigureAwait(false);
            return BadRequest();
        }

        var metadata = await _queries.GetEvEvidenceAsync(scope, environmentId, version, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            await AuditAsync(scope, userId, username, resourceId, false, "not-found-or-not-authorized", correlationId, cancellationToken)
                .ConfigureAwait(false);
            return NotFound(); // mesma resposta para inexistente/cross-tenant/cross-project: anti-IDOR
        }

        var descriptor = new EvDiscoveryEvidenceDescriptor(scope, new EvEnvironmentId(environmentId), version);
        if (!string.Equals(metadata.EvidencePath, descriptor.LogicalPath, StringComparison.Ordinal))
        {
            await AuditAsync(scope, userId, username, resourceId, false, "metadata-path-mismatch", correlationId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning("Evidence download blocked by metadata mismatch. CorrelationId={CorrelationId}", correlationId);
            return StatusCode(StatusCodes.Status409Conflict);
        }

        EvDiscoveryEvidenceContent? content;
        try
        {
            content = await _evidenceStore.GetAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EvDiscoveryEvidenceException)
        {
            await AuditAsync(scope, userId, username, resourceId, false, "bundle-integrity-failed", correlationId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning("Evidence download blocked by bundle integrity validation. CorrelationId={CorrelationId}", correlationId);
            return StatusCode(StatusCodes.Status409Conflict);
        }
        catch (IOException)
        {
            await AuditAsync(scope, userId, username, resourceId, false, "bundle-unavailable", correlationId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning("Evidence download blocked because the bundle is unavailable. CorrelationId={CorrelationId}", correlationId);
            return StatusCode(StatusCodes.Status409Conflict);
        }
        catch (UnauthorizedAccessException)
        {
            await AuditAsync(scope, userId, username, resourceId, false, "bundle-access-denied", correlationId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning("Evidence download blocked by filesystem authorization. CorrelationId={CorrelationId}", correlationId);
            return StatusCode(StatusCodes.Status409Conflict);
        }

        if (content is null)
        {
            await AuditAsync(scope, userId, username, resourceId, false, "bundle-missing", correlationId, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status409Conflict);
        }

        var sizeMatches = content.Bytes.Length == metadata.EvidenceSizeBytes;
        var hashMatches = string.Equals(content.ContentSha256.Value, metadata.ContentSha256, StringComparison.Ordinal);
        if (!sizeMatches || !hashMatches)
        {
            await AuditAsync(scope, userId, username, resourceId, false, "sql-bundle-fingerprint-mismatch", correlationId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning("Evidence download blocked by SQL/bundle fingerprint mismatch. CorrelationId={CorrelationId}", correlationId);
            return StatusCode(StatusCodes.Status409Conflict);
        }

        // A auditoria de SUCESSO ocorre antes da entrega: se persistir a trilha falhar, o download falha fechado.
        await AuditAsync(scope, userId, username, resourceId, true, "verified", correlationId, cancellationToken)
            .ConfigureAwait(false);

        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        return File(content.Bytes.ToArray(), "application/json", "evidence.json");
    }

    private Task AuditAsync(
        ArchiveBridge.Contracts.Jobs.TenantScope scope,
        Guid userId,
        string username,
        string resourceId,
        bool succeeded,
        string reason,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        _operationalAudit.RecordAsync(
            scope,
            new PortalOperationalAuditEvent(
                userId,
                username,
                ActionCode,
                ResourceType,
                resourceId,
                succeeded,
                reason,
                correlationId,
                _clock.UtcNow),
            cancellationToken);
}
