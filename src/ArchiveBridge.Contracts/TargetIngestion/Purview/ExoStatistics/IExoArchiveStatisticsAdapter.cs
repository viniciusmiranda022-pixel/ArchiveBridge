using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;

/// <summary>Um item de estatística de pasta observado pelo adapter — já estruturado (nunca string formatada/localizada, AB-I6-005 item 8).</summary>
public sealed record ExoArchiveFolderStatisticObservation(
    string FolderPath,
    string FolderType,
    long? ItemsInFolder,
    long? ItemsInFolderAndSubfolders,
    long? FolderSizeBytes,
    long? FolderAndSubfolderSizeBytes,
    DateTimeOffset? OldestItemReceivedDateUtc,
    DateTimeOffset? NewestItemReceivedDateUtc);

/// <summary>
/// Resultado bruto de UMA captura de estatísticas de archive EXO (runbook §25.2 "Pré-check do tenant e
/// mailbox" / §26.2 "Estatísticas pós-import") — normalizado, sem tipos do fornecedor (AB-I6-005 item 14).
/// Nunca carrega assunto/corpo/remetente/destinatário/anexo — apenas os metadados agregados documentados
/// aqui.
/// </summary>
public sealed record ExoArchiveStatisticsObservation(
    MailboxArchiveStatus ArchiveStatus,
    Guid? ExchangeGuid,
    Guid? ArchiveGuid,
    long? ItemCount,
    long? TotalItemSizeBytes,
    long? TotalDeletedItemSizeBytes,
    DateTimeOffset? LastLogonTimeUtc,
    bool? RetentionHoldEnabled,
    bool? LitigationHoldEnabled,
    bool? AutoExpandingArchiveEnabled,
    IReadOnlyList<ExoArchiveFolderStatisticObservation> Folders,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Porta substituível para coleta READ-ONLY de estatísticas do archive EXO (runbook §25.2/§26.2, AB-I6-005
/// item 14) — mesmo desenho de <c>IMailboxPrecheckAdapter</c> (I5) e <c>IEvInventoryAdapter</c> (Slice 4C).
/// Este Passo NÃO exige uma implementação real de <c>Get-EXOMailboxStatistics</c>/
/// <c>Get-EXOMailboxFolderStatistics</c> — apenas o contrato e o boundary fail-closed; nenhum adapter
/// fake/estrutural é promovido a produção (item 20 / runbook §25 nota de adapter GA). Nenhuma
/// implementação desta porta pode ter efeito colateral (somente leitura — nenhuma mutação de mailbox/
/// tenant/hold, item 3) nem devolver conteúdo de mailbox.
/// </summary>
public interface IExoArchiveStatisticsAdapter
{
    /// <summary>Sonda as estatísticas atuais do archive de destino — somente leitura, sem efeito colateral.</summary>
    Task<ExoArchiveStatisticsObservation> ObserveAsync(
        TenantScope scope, ArchiveRef archive, ExoStatisticsPhase phase, CorrelationId correlation, CancellationToken cancellationToken);
}
