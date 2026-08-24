using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Contracts.EnterpriseVault.Delta;

/// <summary>Pedido de emissão do watermark INICIAL (baseline) de um archive — entrada opaca ao adapter EV.</summary>
public sealed record EvDeltaBaselineIssueRequest(
    TenantScope Scope, ConnectorId Connector, string ExternalArchiveId, string EvVersionDisplay, Guid ExecutionId, CorrelationId Correlation);

/// <summary>Pedido de emissão do PRÓXIMO watermark a partir do watermark ANTERIOR aceito — entrada opaca ao adapter EV.</summary>
public sealed record EvDeltaIncrementIssueRequest(
    TenantScope Scope, ConnectorId Connector, string ExternalArchiveId, string EvVersionDisplay, EvWatermark Previous, Guid ExecutionId, CorrelationId Correlation);

/// <summary>Resultado da emissão de watermark pelo adapter — o token é opaco fora do próprio adapter que o emitiu.</summary>
public sealed record EvWatermarkIssueResult(string OpaqueToken, string EngineVersion);

/// <summary>
/// Porta substituível (Infrastructure/Connector Host, AB-4C-008 req 7) da delta strategy de uma família de
/// versão EV: emite o token opaco do watermark a partir das APIs/documentação oficiais da versão
/// certificada/compatível. Domain/Application nunca veem o CONTEÚDO do token — apenas a lineage tipada em
/// torno dele (<see cref="EvWatermark"/>). NUNCA usa <c>ReceivedDate</c> isoladamente como único critério
/// de delta (STOP-THE-LINE, runbook §16.5) — a combinação de campos usada é decisão interna do adapter.
/// </summary>
public interface IEvDeltaStrategyAdapter
{
    /// <summary>Descritor (nome+versão) da strategy que este adapter implementa — deve corresponder à seleção determinística do Domain.</summary>
    EvDeltaStrategyId StrategyId { get; }

    /// <summary>Emite o watermark inicial (baseline) — nunca lê nem depende de estado incremental anterior.</summary>
    Task<EvWatermarkIssueResult> IssueBaselineWatermarkAsync(EvDeltaBaselineIssueRequest request, CancellationToken cancellationToken);

    /// <summary>Emite o próximo watermark a partir do watermark ANTERIOR aceito.</summary>
    Task<EvWatermarkIssueResult> IssueIncrementalWatermarkAsync(EvDeltaIncrementIssueRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Catálogo dos adapters de delta strategy disponíveis no Connector Host — resolve o adapter concreto a
/// partir da <see cref="EvDeltaStrategyId"/> escolhida pela seleção determinística do Domain. Ausência de
/// adapter para uma strategy elegível é FALHA FECHADA (nunca um "melhor esforço" com outro adapter).
/// </summary>
public interface IEvDeltaStrategyAdapterCatalog
{
    /// <summary>Resolve o adapter da strategy informada; <see langword="null"/> se nenhum adapter implementa esta strategy.</summary>
    IEvDeltaStrategyAdapter? Resolve(EvDeltaStrategyId strategyId);
}
