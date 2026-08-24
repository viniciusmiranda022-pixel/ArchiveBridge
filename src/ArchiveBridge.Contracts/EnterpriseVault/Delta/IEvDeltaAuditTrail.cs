using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Contracts.EnterpriseVault.Delta;

/// <summary>
/// Evento de custódia/auditoria do ciclo de vida de delta/freeze (AB-4C-008 req 15) — fonte fixa e
/// conhecida, nunca uma string livre.
/// </summary>
public enum EvDeltaAuditEventCode
{
    /// <summary>A seleção determinística de delta strategy resolveu (ou recusou) uma strategy elegível.</summary>
    StrategySelected,

    /// <summary>Uma execução de baseline começou a executar (após capability/strategy revalidadas).</summary>
    BaselineStarted,

    /// <summary>Uma execução de baseline concluiu com sucesso e o primeiro watermark canônico foi persistido.</summary>
    BaselineCompleted,

    /// <summary>Um pedido de delta incremental foi recebido/validado.</summary>
    DeltaRequested,

    /// <summary>Uma execução de delta (ou delta final) concluiu com sucesso e o novo watermark canônico foi persistido.</summary>
    DeltaCompleted,

    /// <summary>Uma execução de delta (ou delta final) falhou (adapter/strategy/watermark).</summary>
    DeltaFailed,

    /// <summary>O adapter EV emitiu um token de watermark (ainda não validado/aceito).</summary>
    WatermarkIssued,

    /// <summary>Um watermark foi validado e aceito como novo canônico.</summary>
    WatermarkAccepted,

    /// <summary>Um watermark candidato foi recusado (stale/cross-scope/downgrade/tampered).</summary>
    WatermarkRejected,

    /// <summary>Um freeze foi solicitado para o archive.</summary>
    FreezeRequested,

    /// <summary>Um freeze foi formalmente autorizado por operador/role competente.</summary>
    FreezeAuthorized,

    /// <summary>Uma autorização de freeze foi recusada.</summary>
    FreezeRejected,

    /// <summary>O delta final concluiu sob freeze autorizado — pronto para cutover.</summary>
    FinalDeltaReady,

    /// <summary>Uma tentativa de avançar o plano além de <see cref="EvFreezeStatus.RollbackRetentionRequired"/> foi bloqueada (sempre, neste Passo).</summary>
    DecommissionBlocked,
}

/// <summary>UM evento de custódia/auditoria de delta/freeze — sem conteúdo de mailbox, credencial ou transcript bruto.</summary>
public sealed record EvDeltaAuditEvent(
    EvDeltaRunId? Run,
    WatermarkId? Watermark,
    FreezePlanId? FreezePlan,
    EvDeltaAuditEventCode EventCode,
    string? Detail,
    CorrelationId Correlation,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Porta de custódia/auditoria append-only do ciclo de vida de delta/freeze. Cada <see cref="AppendAsync"/>
/// grava um evento imutável — evidência anterior nunca é reescrita.
/// </summary>
public interface IEvDeltaAuditTrail
{
    /// <summary>Anexa um evento de custódia/auditoria de delta/freeze.</summary>
    Task AppendAsync(TenantScope scope, EvDeltaAuditEvent auditEvent, CancellationToken cancellationToken);
}
