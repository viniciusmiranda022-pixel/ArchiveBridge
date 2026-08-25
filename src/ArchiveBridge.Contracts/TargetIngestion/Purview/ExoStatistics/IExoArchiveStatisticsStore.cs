using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Porta de persistência dos snapshots de estatísticas de archive EXO e das estatísticas de pasta filhas
/// (AB-I6-005 itens 11-12). Append-only: uma versão nova nunca sobrescreve/edita uma anterior.
/// <paramref name="folders"/> já foi validado/canonicalizado pela Application
/// (<see cref="ExoArchiveFolderStatisticsSet.Canonicalize"/>) antes de qualquer chamada a
/// <see cref="PersistAsync"/> — a store nunca reinterpreta regras de negócio, apenas resolve a versão sob
/// lock e persiste.
/// </summary>
public interface IExoArchiveStatisticsStore
{
    /// <summary>
    /// Aloca a próxima <see cref="ExoArchiveStatisticsSnapshot.SnapshotVersion"/> deste escopo
    /// (tenant/projeto/onda/archive/fase) sob lock — ou converge para uma versão já persistida com o MESMO
    /// <see cref="ExoArchiveStatisticsSnapshot.ObservationHash"/> (item 12, réplay idempotente) — e
    /// persiste, numa única transação curta, o header e as estatísticas de pasta filhas (nunca em
    /// transações separadas — nenhuma versão "parcial" é jamais visível).
    /// </summary>
    Task<ExoArchiveStatisticsSnapshot> PersistAsync(
        TenantScope scope,
        WaveId wave,
        TargetArchiveId archive,
        ExoStatisticsPhase phase,
        MailboxArchiveStatus archiveStatus,
        Guid? exchangeGuid,
        Guid? archiveGuid,
        long? itemCount,
        long? totalItemSizeBytes,
        long? totalDeletedItemSizeBytes,
        DateTimeOffset? lastLogonTimeUtc,
        bool? retentionHoldEnabled,
        bool? litigationHoldEnabled,
        bool? autoExpandingArchiveEnabled,
        IReadOnlyList<ExoArchiveFolderStatistic> folders,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>O snapshot mais recente deste escopo/fase (<see langword="null"/> se nenhum ainda capturado).</summary>
    Task<ExoArchiveStatisticsSnapshot?> GetLatestAsync(
        TenantScope scope, WaveId wave, TargetArchiveId archive, ExoStatisticsPhase phase, CancellationToken cancellationToken);

    /// <summary>
    /// As estatísticas de pasta de uma versão específica, revalidadas (fail-closed) contra a evidência
    /// persistida (contagem + hash agregado) na reidratação — tampering de qualquer pasta nunca é
    /// devolvido como válido.
    /// </summary>
    /// <exception cref="ExoArchiveStatisticsIntegrityViolationException">Pasta(s) adulterada(s) ou hash agregado divergente.</exception>
    Task<IReadOnlyList<ExoArchiveFolderStatistic>> GetFoldersAsync(
        TenantScope scope, WaveId wave, TargetArchiveId archive, ExoStatisticsPhase phase, int snapshotVersion, CancellationToken cancellationToken);
}
