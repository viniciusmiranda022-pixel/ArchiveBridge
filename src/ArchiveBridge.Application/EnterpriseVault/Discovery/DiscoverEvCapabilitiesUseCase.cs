using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>Resultado da execução de descoberta: o resultado avaliado, o registro persistido e se foi reconciliada.</summary>
public sealed record EvDiscoveryOutcome(EvDiscoveryRunResult Result, EvDiscoveryRecord Record, bool Reconciled);

/// <summary>
/// Corpo de execução do comando durável <c>DiscoverEvCapabilities</c>. Sonda o ambiente de forma
/// READ-ONLY (nenhuma modificação no Enterprise Vault, nenhuma exportação), seleciona o adapter por
/// CAPACIDADES (política determinística), avalia o resultado (fail-closed) e persiste a evidência pelo
/// protocolo recuperável em duas transações do Slice 2: encena a evidência canônica (determinística) →
/// reserva a versão (tx1, cercada) → publica a evidência imutável FORA do SQL → finaliza (tx2, cercada,
/// validando a evidência antes de abrir a transação). Uma queda entre as fases é reconciliável pelos
/// hashes (config + evidência) — sem versão indevida; a versão anterior permanece utilizável até a
/// promoção. Quando cercado (<paramref name="fence"/>), o efeito é do dono corrente do Job.
/// </summary>
public sealed class DiscoverEvCapabilitiesUseCase(
    IEvCapabilityDiscovery discovery,
    AdapterCompatibilityEvaluator adapterEvaluator,
    IEvDiscoveryEvidenceSerializer serializer,
    IEvDiscoveryStore store,
    IEvDiscoveryEvidenceStore evidence,
    IClock clock)
{
    private readonly IEvCapabilityDiscovery _discovery = discovery;
    private readonly AdapterCompatibilityEvaluator _adapterEvaluator = adapterEvaluator;
    private readonly IEvDiscoveryEvidenceSerializer _serializer = serializer;
    private readonly IEvDiscoveryStore _store = store;
    private readonly IEvDiscoveryEvidenceStore _evidence = evidence;
    private readonly IClock _clock = clock;

    /// <summary>Executa (ou reconcilia) a descoberta do ambiente e persiste a evidência imutável.</summary>
    public async Task<EvDiscoveryOutcome> ExecuteAsync(
        TenantScope scope,
        EvEnvironmentDescriptor environment,
        EvDiscoveryPolicy policy,
        CorrelationId correlation,
        CancellationToken cancellationToken,
        JobFence? fence = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(policy);

        var startedAtUtc = _clock.UtcNow;
        // Sonda READ-ONLY: nunca modifica o Enterprise Vault, nunca exporta.
        var observation = await _discovery.ProbeAsync(environment, policy, cancellationToken).ConfigureAwait(false);
        var selection = _adapterEvaluator.Select(observation, policy);
        var completedAtUtc = _clock.UtcNow;
        var result = EvDiscoveryEvaluator.Evaluate(
            DiscoveryRunId.New(), observation, selection, policy, startedAtUtc, completedAtUtc);

        var evidenceBytes = _serializer.Serialize(result);

        // Reconciliação: existe uma reserva PENDENTE dos mesmos hashes (queda entre reservar e finalizar)?
        var pending = await _store.GetPendingByHashesAsync(
            scope, environment.EnvironmentId, result.CapabilitySet.ConfigurationHash, result.CapabilitySet.EvidenceHash, cancellationToken)
            .ConfigureAwait(false);

        var staging = await _evidence
            .StageAsync(evidenceBytes.Bytes, evidenceBytes.ContentSha256, cancellationToken).ConfigureAwait(false);
        try
        {
            var reservation = pending
                ?? await _store.ReserveAsync(scope, environment.EnvironmentId, result, staging.SizeBytes, correlation, fence, cancellationToken).ConfigureAwait(false);
            var descriptor = new EvDiscoveryEvidenceDescriptor(scope, environment.EnvironmentId, reservation.DiscoveryVersion);

            // Publica a evidência imutável FORA de qualquer transação SQL (rename atômico; republicação valida o bundle).
            await _evidence.PublishAsync(staging, descriptor, cancellationToken).ConfigureAwait(false);

            var record = await _store.FinalizeAsync(
                scope,
                reservation,
                fence,
                async token =>
                {
                    _ = await _evidence.GetAsync(descriptor, token).ConfigureAwait(false)
                        ?? throw new EvDiscoveryValidationException("Evidência publicada ausente/inválida na finalização (fail-closed).");
                },
                cancellationToken).ConfigureAwait(false);

            return new EvDiscoveryOutcome(result, record, Reconciled: pending is not null);
        }
        catch
        {
            await _evidence.DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
