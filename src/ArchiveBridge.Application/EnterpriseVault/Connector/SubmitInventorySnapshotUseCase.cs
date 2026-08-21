using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Application.EnterpriseVault.Connector;

/// <summary>
/// Solicitação de submissão — <see cref="Scope"/> e <see cref="AuthenticatedConnector"/> são resolvidos
/// juntos pelo composition root a partir do transporte autenticado do connector (mesmo padrão de
/// <c>IPortalScopeAccessor</c> para principals do Portal); nunca informados livremente pelo chamador.
/// </summary>
public sealed record SubmitInventorySnapshotRequest(TenantScope Scope, ConnectorId AuthenticatedConnector, CorrelationId Correlation);

/// <summary>
/// Caso de uso connector-iniciado (AB-4C-001 critérios 6-8, corrigido por AB-4C-002): sonda o inventário via
/// <see cref="IEvInventoryAdapter"/> (porta substituível), normaliza em <see cref="InventorySnapshot"/> e
/// decide, ANTES de qualquer escrita, se o resultado é idêntico ao último snapshot persistido (réplay
/// idempotente, nenhuma linha nova — critério 6) ou se representa mudança real (nova versão, evidência
/// anterior nunca reescrita — critério 7). Connector revogado ou inexistente falha fechado.
/// </summary>
public sealed class SubmitInventorySnapshotUseCase(
    IConnectorRegistry connectors, IConnectorInventoryStore inventory, IEvInventoryAdapter adapter, IClock clock)
{
    /// <summary>
    /// Limite de tentativas de convergência sob corrida (AB-4C-002): cada tentativa releé o latest e tenta
    /// a próxima versão livre. Alto o bastante para absorver contenção real entre poucos writers
    /// concorrentes do MESMO connector, baixo o bastante para falhar fechado (nunca travar indefinidamente)
    /// se a contenção for anômala.
    /// </summary>
    private const int MaxConvergenceAttempts = 8;

    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IConnectorInventoryStore _inventory = inventory;
    private readonly IEvInventoryAdapter _adapter = adapter;
    private readonly IClock _clock = clock;

    /// <summary>Sonda, normaliza e persiste (ou converge por réplay) o inventário do connector autenticado.</summary>
    /// <exception cref="ConnectorNotFoundException">Connector inexistente.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    /// <exception cref="ConcurrencyException">
    /// Contenção persistente: <see cref="MaxConvergenceAttempts"/> tentativas não convergiram (nunca perde
    /// a mudança em silêncio — falha fechado para o chamador decidir se tenta de novo).
    /// </exception>
    public async Task<InventorySnapshotAppendResult> ExecuteAsync(
        SubmitInventorySnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identity = await _connectors.GetAsync(request.Scope, request.AuthenticatedConnector, cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectorNotFoundException();
        if (!identity.IsActive)
        {
            throw new ConnectorRevokedException(identity.Id);
        }

        // A sondagem em si (única, não repetida por tentativa de convergência) é o único contato com o
        // adapter — retries abaixo disputam apenas a VERSÃO de persistência do mesmo resultado já sondado,
        // nunca reexecutam a sondagem contra o Enterprise Vault.
        var probe = await _adapter.ProbeAsync(identity.Id, request.Correlation, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
        {
            var latest = await _inventory.GetLatestAsync(request.Scope, identity.Id, cancellationToken).ConfigureAwait(false);
            var candidateVersion = (latest?.Version ?? 0) + 1;
            var candidate = InventorySnapshot.Create(
                InventorySnapshotId.New(), identity.Id, identity.Tenant, identity.Project, candidateVersion,
                probe.Archives, request.Correlation, now);

            if (latest is not null && latest.SnapshotHash == candidate.SnapshotHash)
            {
                // Réplay idempotente: nenhuma mudança real, nenhuma linha nova, nenhuma reexecução de efeito.
                return new InventorySnapshotAppendResult(latest, Created: false);
            }

            try
            {
                return await _inventory.AppendAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException) when (attempt < MaxConvergenceAttempts)
            {
                // Outra submissão concorrente do MESMO connector ocupou candidateVersion com conteúdo
                // DIFERENTE (AB-4C-002) — nunca tratado como réplay pelo store. Releé o latest agora
                // atualizado e tenta de novo com a próxima versão livre; a mudança nunca é descartada.
            }
        }

        throw new ConcurrencyException(
            $"Connector {identity.Id.Value}: não foi possível convergir o snapshot de inventário após " +
            $"{MaxConvergenceAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} tentativas concorrentes.");
    }
}
