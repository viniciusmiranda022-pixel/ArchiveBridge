using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.EnterpriseVault.Export;

/// <summary>
/// Solicitação de exportação. <see cref="MaxPstSizeMb"/>/<see cref="MaxThreads"/> são OPCIONAIS — quando
/// omitidos, o default do produto (<see cref="ExportRequestPolicy.Default"/>) é usado; quando informados,
/// devem estar dentro do envelope documentado do cmdlet E do envelope EFETIVO deste deployment.
/// </summary>
public sealed record RequestEvExport(
    TenantScope Scope,
    ConnectorId Connector,
    string ExternalArchiveId,
    int? MaxPstSizeMb,
    int? MaxThreads,
    string RequestedBy,
    CorrelationId Correlation);

/// <summary>Resultado da solicitação: o pedido/Job durável criado (ou reaproveitado por idempotência canônica).</summary>
public sealed record EvExportRequestResult(ExportRequestId RequestId, JobId JobId, bool Created, bool Replayed);

/// <summary>
/// Caso de uso de SOLICITAÇÃO de exportação EV (AB-4C-005). NÃO executa o exporter nem espera o resultado:
/// revalida capability/support-matrix do Passo 1 (item 8), valida a política contra o envelope efetivo
/// (item 3), computa a identidade CANÔNICA do pedido (item 5) e enfileira de forma durável e idempotente. O
/// worker processa depois. Connector inexistente, revogado ou fora do escopo é indistinguível de
/// inexistente (anti-IDOR).
/// </summary>
public sealed class RequestEvExportUseCase(
    IConnectorRegistry connectors,
    IConnectorCapabilityStore capabilities,
    IEvExportCommandInbox inbox,
    IEvExportAuditTrail audit,
    EvExportPolicy policy,
    IClock clock)
{
    private const int RequestedByMaxLength = 200;
    private const int ExternalArchiveIdMaxLength = 300;

    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IConnectorCapabilityStore _capabilities = capabilities;
    private readonly IEvExportCommandInbox _inbox = inbox;
    private readonly IEvExportAuditTrail _audit = audit;
    private readonly EvExportPolicy _policy = policy;
    private readonly IClock _clock = clock;

    /// <summary>Valida capability/política server-side e enfileira o pedido idempotente; devolve o pedido/Job.</summary>
    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    /// <exception cref="EvExportCapabilityBlockedException">Capability de exportação não certificada/ausente.</exception>
    /// <exception cref="EvExportValidationException">Política acima do envelope efetivo aprovado.</exception>
    public async Task<EvExportRequestResult> ExecuteAsync(RequestEvExport request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var externalArchiveId = SanitizeArchiveId(request.ExternalArchiveId);
        var requestedBy = SanitizeRequestedBy(request.RequestedBy);

        var identity = await _connectors.GetAsync(request.Scope, request.Connector, cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectorNotFoundException();
        if (!identity.IsActive)
        {
            throw new ConnectorRevokedException(identity.Id);
        }

        // Revalida a capability/support-matrix do Passo 1 (item 8): capability ausente/não certificada
        // bloqueia fail-closed ANTES de qualquer enfileiramento.
        var handshake = await _capabilities.GetLatestAsync(request.Scope, identity.Id, cancellationToken).ConfigureAwait(false);
        if (handshake is null || !handshake.ExportCapable)
        {
            throw new EvExportCapabilityBlockedException(handshake?.BlockingReason ?? "NO_CAPABILITY_HANDSHAKE");
        }

        var requestedPolicy = ExportRequestPolicy.Create(
            request.MaxPstSizeMb ?? ExportRequestPolicy.DefaultMaxPstSizeMb,
            request.MaxThreads ?? ExportRequestPolicy.DefaultMaxThreads);
        _policy.EnsureWithinEffectiveEnvelope(requestedPolicy);

        var canonicalIdentity = EvExportRequestIdentity.Compute(
            identity.Tenant, identity.Project, identity.Id, externalArchiveId, requestedPolicy);

        var command = new EvExportCommand(
            request.Scope, identity.Id, externalArchiveId, requestedPolicy.MaxThreads, requestedPolicy.MaxPstSizeMb,
            requestedBy, request.Correlation);

        var enqueued = await _inbox
            .EnqueueIdempotentAsync(command, canonicalIdentity.ToIdempotencyKey(), cancellationToken).ConfigureAwait(false);

        await _audit.AppendAsync(
            request.Scope,
            new EvExportAuditEvent(
                enqueued.RequestId, null, EvExportAuditEventCode.Requested,
                enqueued.Replayed ? "REPLAYED" : "CREATED", request.Correlation, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return new EvExportRequestResult(enqueued.RequestId, enqueued.JobId, enqueued.Created, enqueued.Replayed);
    }

    private static string SanitizeRequestedBy(string requestedBy)
    {
        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            throw new ArgumentException("O solicitante (identidade autenticada) é obrigatório.", nameof(requestedBy));
        }

        var cleaned = new string(requestedBy.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            throw new ArgumentException("O solicitante é inválido após higienização.", nameof(requestedBy));
        }

        return cleaned.Length > RequestedByMaxLength ? cleaned[..RequestedByMaxLength] : cleaned;
    }

    private static string SanitizeArchiveId(string externalArchiveId)
    {
        if (string.IsNullOrWhiteSpace(externalArchiveId))
        {
            throw new ArgumentException("O archive-alvo é obrigatório.", nameof(externalArchiveId));
        }

        var trimmed = externalArchiveId.Trim();
        return trimmed.Length > ExternalArchiveIdMaxLength ? trimmed[..ExternalArchiveIdMaxLength] : trimmed;
    }
}
