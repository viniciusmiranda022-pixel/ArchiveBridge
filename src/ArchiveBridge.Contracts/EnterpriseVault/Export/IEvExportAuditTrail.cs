using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>
/// Evento de custódia/auditoria do ciclo de vida de exportação (AB-4C-005 item 17) — fonte fixa e conhecida,
/// nunca uma string livre.
/// </summary>
public enum EvExportAuditEventCode
{
    /// <summary>O pedido de exportação foi recebido e enfileirado (ou reconciliado por idempotência).</summary>
    Requested,

    /// <summary>Uma tentativa começou a executar (após capability revalidada e leases de throttle adquiridos).</summary>
    Started,

    /// <summary>Uma tentativa foi bloqueada por concorrência não autorizada (connector ou archive já em execução).</summary>
    Throttled,

    /// <summary>Uma nova tentativa foi agendada após falha transitória.</summary>
    RetryScheduled,

    /// <summary>Uma tentativa concluiu com sucesso e o manifesto canônico foi persistido.</summary>
    Completed,

    /// <summary>Uma tentativa falhou (processo/capability/timeout).</summary>
    Failed,

    /// <summary>Outputs PST foram descobertos e hasheados no diretório de saída.</summary>
    OutputDiscovered,

    /// <summary>Um ou mais itens nativos oversized foram detectados.</summary>
    OversizedDetected,

    /// <summary>A revalidação de integridade dos outputs físicos falhou (replay/tampering).</summary>
    IntegrityFailed,
}

/// <summary>UM evento de custódia/auditoria de exportação — sem conteúdo de mailbox, credencial ou transcript bruto.</summary>
public sealed record EvExportAuditEvent(
    ExportRequestId Request,
    ExportAttemptId? Attempt,
    EvExportAuditEventCode EventCode,
    string? Detail,
    CorrelationId Correlation,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Porta de custódia/auditoria append-only do ciclo de vida de exportação. Cada
/// <see cref="AppendAsync"/> grava um evento imutável — evidência anterior nunca é reescrita.
/// </summary>
public interface IEvExportAuditTrail
{
    /// <summary>Anexa um evento de custódia/auditoria de exportação.</summary>
    Task AppendAsync(TenantScope scope, EvExportAuditEvent auditEvent, CancellationToken cancellationToken);
}
