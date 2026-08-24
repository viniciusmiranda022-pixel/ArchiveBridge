using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>
/// Passos compartilhados entre os casos de uso de solicitação de fase de delta (AB-4C-008) — resolução de
/// connector/capability, descrição de seleção de strategy para auditoria e convergência idempotente de
/// tentativas sob corrida (mesmo padrão de <c>SubmitInventorySnapshotUseCase</c>).
/// </summary>
internal static class EvDeltaExecutionSupport
{
    private const int ExternalArchiveIdMaxLength = 300;
    private const int ReasonMaxLength = 300;
    private const int MaxConvergenceAttempts = 8;

    /// <summary>Resolve o connector autenticado no escopo e garante que está ativo (fail-closed em revogado/inexistente).</summary>
    public static async Task<ConnectorIdentity> ResolveActiveConnectorAsync(
        IConnectorRegistry connectors, TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        var identity = await connectors.GetAsync(scope, connector, cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectorNotFoundException();
        if (!identity.IsActive)
        {
            throw new ConnectorRevokedException(identity.Id);
        }

        return identity;
    }

    /// <summary>Revalida a capability EV do connector — reaproveita o MESMO gate do Passo 2 (export capability).</summary>
    public static async Task<ConnectorCapabilityHandshake> RequireExportCapableAsync(
        IConnectorCapabilityStore capabilities, TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        var handshake = await capabilities.GetLatestAsync(scope, connector, cancellationToken).ConfigureAwait(false);
        if (handshake is null || !handshake.ExportCapable)
        {
            throw new EvExportCapabilityBlockedException(handshake?.BlockingReason ?? "NO_CAPABILITY_HANDSHAKE");
        }

        return handshake;
    }

    /// <summary>Sanitiza o archive-alvo (mesma regra de <c>RequestEvExportUseCase</c>).</summary>
    public static string SanitizeArchiveId(string externalArchiveId)
    {
        if (string.IsNullOrWhiteSpace(externalArchiveId))
        {
            throw new ArgumentException("O archive-alvo é obrigatório.", nameof(externalArchiveId));
        }

        var trimmed = externalArchiveId.Trim();
        return trimmed.Length > ExternalArchiveIdMaxLength ? trimmed[..ExternalArchiveIdMaxLength] : trimmed;
    }

    /// <summary>Descrição estável (nunca mensagem livre interpolando dado do ambiente) da seleção, para auditoria.</summary>
    public static string DescribeSelection(EvDeltaStrategySelection selection) =>
        selection.Outcome == EvDeltaStrategySelectionOutcome.Supported && selection.Selected is not null
            ? selection.Selected.StrategyId.DisplayName
            : selection.Outcome.ToString();

    /// <summary>Trunca uma mensagem de exceção do adapter para um motivo de bloqueio seguro (nunca stack trace/segredo).</summary>
    public static string TruncateReason(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "ADAPTER_FAILURE" : (message.Length > ReasonMaxLength ? message[..ReasonMaxLength] : message);

    /// <summary>
    /// Anexa a tentativa (e, quando informado, o watermark) sob a chave de idempotência canônica,
    /// convergindo sob corrida (AB-4C-008 acceptance: baseline/delta idempotente e concorrente ⇒ um único
    /// efeito lógico): ao colidir, relê a tentativa vigente — se já TERMINAL, devolve-a como replay; senão
    /// tenta de novo sob o Run agora conhecido. Nunca perde a mudança em silêncio.
    /// </summary>
    public static async Task<EvDeltaAttemptRecord> AppendAttemptWithConvergenceAsync(
        IEvDeltaRunStore runs,
        TenantScope scope,
        Guid idempotencyKey,
        EvDeltaAttemptCandidate candidate,
        Domain.EnterpriseVault.Delta.EvWatermark? watermarkToPersist,
        CancellationToken cancellationToken)
    {
        var current = candidate;
        for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
        {
            try
            {
                return await runs.AppendAttemptAsync(scope, idempotencyKey, current, watermarkToPersist, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException) when (attempt < MaxConvergenceAttempts)
            {
                var latest = await runs.GetLatestByIdempotencyKeyAsync(scope, idempotencyKey, cancellationToken).ConfigureAwait(false);
                if (latest is not null && EvDeltaRunOutcomes.IsTerminal(latest.Outcome))
                {
                    // Outra execução concorrente já convergiu para um desfecho terminal — replay, nunca duplica efeito.
                    return latest;
                }

                current = current with { ExistingRun = latest?.Run };
            }
        }

        throw new ConcurrencyException("Não foi possível convergir a tentativa de delta após múltiplas tentativas concorrentes.");
    }

    /// <summary>Projeta uma tentativa persistida no resultado uniforme do caso de uso.</summary>
    public static EvDeltaRunResult ToResult(EvDeltaAttemptRecord record, bool replayed) =>
        new(record.Run, record.Attempt, record.AttemptNumber, record.Outcome, record.IssuedWatermark, replayed);
}
