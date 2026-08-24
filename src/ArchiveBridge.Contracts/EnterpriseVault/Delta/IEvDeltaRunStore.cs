using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Contracts.EnterpriseVault.Delta;

/// <summary>UMA tentativa persistida (append-only) de execução de fase de delta — nunca reescrita; a história completa é sempre preservada (req 12/14).</summary>
public sealed record EvDeltaAttemptRecord(
    EvDeltaRunId Run,
    EvDeltaAttemptId Attempt,
    int AttemptNumber,
    ConnectorId Connector,
    string ExternalArchiveId,
    EvDeltaPhase Phase,
    EvDeltaStrategyId? Strategy,
    WatermarkId? PreviousWatermark,
    WatermarkId? IssuedWatermark,
    EvDeltaRunOutcome Outcome,
    string? BlockingReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>Candidato a NOVA tentativa — o store atribui <see cref="EvDeltaAttemptRecord.Attempt"/>/<see cref="EvDeltaAttemptRecord.AttemptNumber"/>.</summary>
public sealed record EvDeltaAttemptCandidate(
    EvDeltaRunId? ExistingRun,
    ConnectorId Connector,
    string ExternalArchiveId,
    EvDeltaPhase Phase,
    EvDeltaStrategyId? Strategy,
    WatermarkId? PreviousWatermark,
    WatermarkId? IssuedWatermark,
    EvDeltaRunOutcome Outcome,
    string? BlockingReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Store append-only da história de tentativas de execução de fase de delta (AB-4C-008 req 5/12/14),
/// indexado por uma chave de idempotência CANÔNICA (<see cref="EvDeltaRunIdentity"/>): o MESMO
/// phase+watermark+archive (+strategy) converge SEMPRE para o MESMO <see cref="EvDeltaRunId"/> — nunca cria
/// um segundo Run para a mesma operação lógica. Toda leitura é escopada (anti-IDOR).
/// </summary>
public interface IEvDeltaRunStore
{
    /// <summary>
    /// Devolve a tentativa mais recente já persistida sob a chave de idempotência canônica; <see langword="null"/>
    /// se nenhuma existir ainda. Um desfecho TERMINAL (<see cref="EvDeltaRunOutcomes.IsTerminal"/>) nunca deve
    /// ser reexecutado pela Application — apenas replayed. Um desfecho <see cref="EvDeltaRunOutcome.Failed"/>
    /// (retryable) autoriza uma NOVA tentativa sob o MESMO <see cref="EvDeltaRunId"/>.
    /// </summary>
    Task<EvDeltaAttemptRecord?> GetLatestByIdempotencyKeyAsync(TenantScope scope, Guid canonicalIdempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Insere uma NOVA tentativa (append-only) sob a chave de idempotência informada — <c>attempt_number</c>
    /// é computado no servidor sob lock. Quando <paramref name="watermarkToPersist"/> é informado (SOMENTE
    /// para <see cref="EvDeltaRunOutcome.Completed"/>, com <see cref="EvWatermark.Id"/> igual a
    /// <see cref="EvDeltaAttemptCandidate.IssuedWatermark"/>), a tentativa e o watermark são persistidos na
    /// MESMA transação — o watermark só se torna canônico se a tentativa também for gravada (req 6/14): um
    /// crash entre a emissão do token pelo adapter e este commit nunca deixa um watermark "órfão" nem
    /// avança o checkpoint sem evidência. Sob corrida pela MESMA chave e MESMO próximo
    /// <c>attempt_number</c>, apenas uma tentativa vence; a perdedora recebe
    /// <see cref="ArchiveBridge.Domain.Common.ConcurrencyException"/> e deve reler via
    /// <see cref="GetLatestByIdempotencyKeyAsync"/> antes de decidir de novo (mesmo padrão de
    /// <c>IConnectorInventoryStore.AppendAsync</c>) — a mudança nunca é perdida em silêncio.
    /// </summary>
    /// <exception cref="ArchiveBridge.Domain.Common.ConcurrencyException">A tentativa colidiu com uma gravação concorrente (retriable).</exception>
    Task<EvDeltaAttemptRecord> AppendAttemptAsync(
        TenantScope scope, Guid canonicalIdempotencyKey, EvDeltaAttemptCandidate candidate, EvWatermark? watermarkToPersist, CancellationToken cancellationToken);

    /// <summary>Devolve TODA a história de tentativas do Run, em ordem de <c>AttemptNumber</c> crescente (evidência/auditoria).</summary>
    Task<IReadOnlyList<EvDeltaAttemptRecord>> ListAttemptsAsync(TenantScope scope, EvDeltaRunId run, CancellationToken cancellationToken);
}
