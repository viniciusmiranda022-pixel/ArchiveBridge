using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview;

/// <summary>Resultado bruto de UMA sondagem de precheck de mailbox — normalizado, sem tipos do fornecedor.</summary>
public sealed record MailboxPrecheckObservation(
    Guid? ExchangeGuid,
    Guid? ArchiveGuid,
    MailboxArchiveStatus ArchiveStatus,
    string? RecipientTypeDetails,
    bool AutoExpandingArchiveEnabled,
    bool LitigationHoldEnabled,
    bool RetentionHoldEnabled,
    long? ArchiveItemCount,
    long? ArchiveTotalSizeBytes,
    long? ObservedAvailableBytes,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Porta substituível de precheck read-only de tenant/mailbox (runbook §25.2, work order AB-I5-001 item 4).
/// Este Passo NÃO exige uma implementação real de <c>Get-EXOMailbox</c>/<c>Get-EXOMailboxStatistics</c> —
/// apenas o contrato e um adapter de teste/fixture determinístico (ver Application.Tests/Integration.Tests),
/// mesmo desenho de <c>IEvInventoryAdapter</c> (Slice 4C Passo 1). Nenhuma implementação desta porta pode
/// ter efeito colateral (somente leitura — nenhuma mutação de tenant/mailbox, work order item 5) nem
/// devolver conteúdo de mailbox (assunto/corpo/remetente/destinatário/anexo).
/// </summary>
public interface IMailboxPrecheckAdapter
{
    /// <summary>Sonda o precheck atual da mailbox de destino — somente leitura, sem efeito colateral.</summary>
    Task<MailboxPrecheckObservation> ObserveAsync(
        TenantScope scope, ArchiveRef mailbox, CorrelationId correlation, CancellationToken cancellationToken);
}
