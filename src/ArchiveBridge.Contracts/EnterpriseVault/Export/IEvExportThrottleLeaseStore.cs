using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>Um lease de throttling adquirido — deve ser SEMPRE liberado (bloco <c>finally</c>), mesmo em falha.</summary>
public sealed record EvExportThrottleLease(Guid LeaseId, ConnectorId Connector, string ExternalArchiveId);

/// <summary>
/// Porta de throttling/scheduling de exportação (AB-4C-005 item 4; recuperável desde AB-4C-007 blocker 1):
/// impede concorrência não autorizada por CONNECTOR e por ARCHIVE simultaneamente. Uma tentativa só é
/// iniciada depois de adquirir os DOIS leases; se qualquer um já estiver em uso por outra tentativa em
/// andamento (e ainda dentro do seu deadline), a aquisição falha (a chamada NÃO bloqueia/espera — a
/// Application decide reagendar via retry do Job). O backstop de corrida é sempre a linha SQL do slot sob
/// lock — duas aquisições concorrentes para o MESMO connector (ou o MESMO archive) nunca convergem em duas
/// tentativas simultâneas. Um lease NUNCA fica órfão permanentemente: cada aquisição tem um deadline
/// durável (<paramref name="leaseDuration"/> em <see cref="TryAcquireAsync"/>) e um titular expirado é
/// automaticamente recuperável pela PRÓXIMA aquisição — nenhum reaper/lock em memória separado é
/// necessário. Durante uma execução mais longa que o deadline, o chamador DEVE renovar periodicamente via
/// <see cref="TryRenewAsync"/> (mesmo padrão de heartbeat de <c>IJobLeaseManager.RenewAsync</c>); a perda da
/// renovação (deadline vencido ou titular trocado) é fail-closed — nenhum efeito novo deve ser persistido.
/// </summary>
public interface IEvExportThrottleLeaseStore
{
    /// <summary>
    /// Tenta adquirir, atomicamente, o lease de connector E de archive para a tentativa informada, com
    /// deadline <c>agora + <paramref name="leaseDuration"/></c>. <see langword="null"/> quando qualquer um
    /// dos dois já está em uso por um lease AINDA válido — nenhum efeito parcial é deixado (a tentativa
    /// nunca adquire só um dos dois leases). Um slot cujo titular anterior expirou é recuperado
    /// transparentemente pela mesma chamada (nunca precisa de um reaper à parte).
    /// </summary>
    Task<EvExportThrottleLease?> TryAcquireAsync(
        TenantScope scope, ConnectorId connector, string externalArchiveId, ExportAttemptId attempt,
        CorrelationId correlation, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// Renova (heartbeat) o deadline dos DOIS slots do lease para <c>agora + <paramref name="leaseDuration"/></c>,
    /// atomicamente (os dois avançam juntos, ou nenhum avança). <see langword="false"/> quando o lease já
    /// expirou OU foi reassumido por outro titular (o <c>lease_id</c> não corresponde mais) — o chamador
    /// DEVE tratar como cercamento perdido e cancelar/fencear a operação em curso antes de persistir
    /// qualquer efeito novo (nunca ressuscita um lease já vencido).
    /// </summary>
    Task<bool> TryRenewAsync(
        TenantScope scope, EvExportThrottleLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// Libera o lease adquirido (idempotente — liberar um lease já liberado, expirado ou reassumido a outro
    /// titular não lança e não afeta o titular atual).
    /// </summary>
    Task ReleaseAsync(TenantScope scope, EvExportThrottleLease lease, CancellationToken cancellationToken);
}
