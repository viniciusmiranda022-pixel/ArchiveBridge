using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>Um lease de throttling adquirido — deve ser SEMPRE liberado (bloco <c>finally</c>), mesmo em falha.</summary>
public sealed record EvExportThrottleLease(Guid LeaseId, ConnectorId Connector, string ExternalArchiveId);

/// <summary>
/// Porta de throttling/scheduling de exportação (AB-4C-005 item 4): impede concorrência não autorizada por
/// CONNECTOR e por ARCHIVE simultaneamente. Uma tentativa só é iniciada depois de adquirir os DOIS leases;
/// se qualquer um já estiver em uso por outra tentativa em andamento, a aquisição falha (a chamada NÃO
/// bloqueia/espera — a Application decide reagendar via retry do Job). O backstop de corrida é sempre um
/// índice único SQL — duas aquisições concorrentes para o MESMO connector (ou o MESMO archive) nunca
/// convergem em duas tentativas simultâneas.
/// </summary>
public interface IEvExportThrottleLeaseStore
{
    /// <summary>
    /// Tenta adquirir, atomicamente, o lease de connector E de archive para a tentativa informada.
    /// <see langword="null"/> quando qualquer um dos dois já está em uso (throttled) — nenhum efeito
    /// parcial é deixado (a tentativa nunca adquire só um dos dois leases).
    /// </summary>
    Task<EvExportThrottleLease?> TryAcquireAsync(
        TenantScope scope, ConnectorId connector, string externalArchiveId, ExportAttemptId attempt,
        CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>Libera o lease adquirido (idempotente — liberar um lease já liberado não lança).</summary>
    Task ReleaseAsync(TenantScope scope, EvExportThrottleLease lease, CancellationToken cancellationToken);
}
