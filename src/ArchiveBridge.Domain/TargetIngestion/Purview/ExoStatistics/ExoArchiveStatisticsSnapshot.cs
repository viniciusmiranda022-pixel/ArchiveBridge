using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Snapshot IMUTÁVEL e append-only de UMA observação de estatísticas do archive EXO — <see cref="Phase"/>
/// <see cref="ExoStatisticsPhase.BeforeImport"/> ou <see cref="ExoStatisticsPhase.AfterImport"/> (runbook
/// §25.2/§26.2, AB-I6-005 itens 1/6/11) — vinculado server-side a TenantScope + Project + Wave +
/// <see cref="Archive"/>. Estritamente read-only: nenhum campo representa ou habilita mutação de mailbox/
/// tenant/hold; <see cref="RetentionHoldEnabled"/>/<see cref="LitigationHoldEnabled"/>/
/// <see cref="AutoExpandingArchiveEnabled"/> são observações, nunca comandos. Campo ausente do provider é
/// <see langword="null"/> (Unknown/NotReported) — NUNCA zero/false/data mínima (item 7). As estatísticas
/// de pasta filhas (<see cref="ExoArchiveFolderStatistic"/>) vivem em tabela própria; este header carrega
/// apenas <see cref="FolderCount"/>/<see cref="FoldersSha256"/> (mesmo desenho de
/// <c>ServiceResult.PurviewServiceResultReportEvidence</c> + linhas filhas).
/// <para>
/// Versionamento monotônico por (tenant, projeto, onda, archive, fase): a MESMA observação lógica
/// (<see cref="ObservationHash"/>, que inclui <see cref="ObservedAtUtc"/> — mesmo princípio de
/// <c>MailboxPrecheckSnapshot.IsSameContentAs</c>) converge para a MESMA versão; mudança real produz N+1
/// (item 12).
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="ObservationHash"/>
/// e <see cref="SnapshotHash"/> a partir dos campos REALMENTE carregados e recusa fail-closed qualquer
/// divergência (item 11).
/// </para>
/// </summary>
public sealed record ExoArchiveStatisticsSnapshot
{
    private ExoArchiveStatisticsSnapshot(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        TargetArchiveId archive,
        ExoStatisticsPhase phase,
        int snapshotVersion,
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
        int folderCount,
        Sha256Hash foldersSha256,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc,
        Sha256Hash observationHash,
        Sha256Hash snapshotHash)
    {
        Tenant = tenant;
        Project = project;
        Wave = wave;
        Archive = archive;
        Phase = phase;
        SnapshotVersion = snapshotVersion;
        ArchiveStatus = archiveStatus;
        ExchangeGuid = exchangeGuid;
        ArchiveGuid = archiveGuid;
        ItemCount = itemCount;
        TotalItemSizeBytes = totalItemSizeBytes;
        TotalDeletedItemSizeBytes = totalDeletedItemSizeBytes;
        LastLogonTimeUtc = lastLogonTimeUtc;
        RetentionHoldEnabled = retentionHoldEnabled;
        LitigationHoldEnabled = litigationHoldEnabled;
        AutoExpandingArchiveEnabled = autoExpandingArchiveEnabled;
        FolderCount = folderCount;
        FoldersSha256 = foldersSha256;
        ObservedAtUtc = observedAtUtc;
        Correlation = correlation;
        CreatedAtUtc = createdAtUtc;
        ObservationHash = observationHash;
        SnapshotHash = snapshotHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Identidade canônica do archive de destino, resolvida server-side (nunca fornecida como autoridade pelo caller).</summary>
    public TargetArchiveId Archive { get; }

    /// <summary>Fase da observação (BeforeImport/AfterImport).</summary>
    public ExoStatisticsPhase Phase { get; }

    /// <summary>Versão monotônica (1..N) deste snapshot dentro de (tenant, projeto, onda, archive, fase).</summary>
    public int SnapshotVersion { get; }

    /// <summary>Status observado do Online Archive — <see cref="MailboxArchiveStatus.Unknown"/> por default fail-closed.</summary>
    public MailboxArchiveStatus ArchiveStatus { get; }

    /// <summary>GUID de mailbox observado, quando disponível.</summary>
    public Guid? ExchangeGuid { get; }

    /// <summary>GUID de archive observado, quando disponível.</summary>
    public Guid? ArchiveGuid { get; }

    /// <summary>Contagem de itens do archive, ou <see langword="null"/> quando não fornecida (Unknown/NotReported).</summary>
    public long? ItemCount { get; }

    /// <summary>Tamanho total do archive em bytes, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public long? TotalItemSizeBytes { get; }

    /// <summary>Tamanho total de itens excluídos (dumpster) em bytes, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public long? TotalDeletedItemSizeBytes { get; }

    /// <summary>Último logon observado, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public DateTimeOffset? LastLogonTimeUtc { get; }

    /// <summary>Retention hold observado (read-only) — <see langword="null"/> quando o provider não reportou.</summary>
    public bool? RetentionHoldEnabled { get; }

    /// <summary>Litigation hold observado (read-only) — <see langword="null"/> quando o provider não reportou.</summary>
    public bool? LitigationHoldEnabled { get; }

    /// <summary>Auto-expanding archive observado (read-only, NUNCA habilitado por este Passo) — <see langword="null"/> quando o provider não reportou.</summary>
    public bool? AutoExpandingArchiveEnabled { get; }

    /// <summary>Quantidade de estatísticas de pasta filhas persistidas para esta versão.</summary>
    public int FolderCount { get; }

    /// <summary>Hash agregado determinístico das estatísticas de pasta filhas (<see cref="ExoArchiveFolderStatisticsHash"/>) — revalidado na leitura.</summary>
    public Sha256Hash FoldersSha256 { get; }

    /// <summary>Instante em que o adapter capturou a observação (UTC).</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Correlação com a trilha de auditoria/evidência.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// Hash determinístico do CONTEÚDO lógico da observação — usado como chave de convergência idempotente
    /// (item 12): a MESMA observação lógica produz o MESMO hash independentemente da versão/instante de
    /// persistência.
    /// </summary>
    public Sha256Hash ObservationHash { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash SnapshotHash { get; }

    /// <summary>Cria um novo snapshot, computando <see cref="ObservationHash"/>/<see cref="SnapshotHash"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="snapshotVersion"/> não é positivo, um contador é negativo, ou <paramref name="folderCount"/> é negativo.</exception>
    public static ExoArchiveStatisticsSnapshot Create(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        TargetArchiveId archive,
        ExoStatisticsPhase phase,
        int snapshotVersion,
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
        int folderCount,
        Sha256Hash foldersSha256,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc)
    {
        if (snapshotVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotVersion), snapshotVersion, "A versão do snapshot deve ser positiva.");
        }

        RequireNonNegative(itemCount, nameof(itemCount));
        RequireNonNegative(totalItemSizeBytes, nameof(totalItemSizeBytes));
        RequireNonNegative(totalDeletedItemSizeBytes, nameof(totalDeletedItemSizeBytes));
        if (folderCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(folderCount), folderCount, "FolderCount não pode ser negativo.");
        }

        var canonicalLastLogon = lastLogonTimeUtc is { } logon ? TruncateToMilliseconds(logon) : (DateTimeOffset?)null;
        var canonicalObservedAt = TruncateToMilliseconds(observedAtUtc);
        var canonicalCreatedAt = TruncateToMilliseconds(createdAtUtc);

        var observationHash = ComputeObservationHash(
            tenant, project, wave, archive, phase, archiveStatus, exchangeGuid, archiveGuid, itemCount,
            totalItemSizeBytes, totalDeletedItemSizeBytes, canonicalLastLogon, retentionHoldEnabled,
            litigationHoldEnabled, autoExpandingArchiveEnabled, foldersSha256, folderCount, canonicalObservedAt);
        var snapshotHash = ComputeSnapshotHash(snapshotVersion, canonicalCreatedAt, correlation, observationHash);

        return new ExoArchiveStatisticsSnapshot(
            tenant, project, wave, archive, phase, snapshotVersion, archiveStatus, exchangeGuid, archiveGuid,
            itemCount, totalItemSizeBytes, totalDeletedItemSizeBytes, canonicalLastLogon, retentionHoldEnabled,
            litigationHoldEnabled, autoExpandingArchiveEnabled, folderCount, foldersSha256, canonicalObservedAt,
            correlation, canonicalCreatedAt, observationHash, snapshotHash);
    }

    /// <summary>
    /// Reconstrói uma versão JÁ PERSISTIDA (uso exclusivo da camada de persistência), revalidando
    /// <see cref="ObservationHash"/> e <see cref="SnapshotHash"/> contra os campos REALMENTE carregados
    /// (fail-closed).
    /// </summary>
    /// <exception cref="ExoArchiveStatisticsIntegrityViolationException">Um dos hashes persistidos diverge do recomputado.</exception>
    public static ExoArchiveStatisticsSnapshot Rehydrate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        TargetArchiveId archive,
        ExoStatisticsPhase phase,
        int snapshotVersion,
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
        int folderCount,
        Sha256Hash foldersSha256,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset createdAtUtc,
        Sha256Hash persistedObservationHash,
        Sha256Hash persistedSnapshotHash)
    {
        var recomputedObservationHash = ComputeObservationHash(
            tenant, project, wave, archive, phase, archiveStatus, exchangeGuid, archiveGuid, itemCount,
            totalItemSizeBytes, totalDeletedItemSizeBytes, lastLogonTimeUtc, retentionHoldEnabled,
            litigationHoldEnabled, autoExpandingArchiveEnabled, foldersSha256, folderCount, observedAtUtc);
        if (!string.Equals(recomputedObservationHash.Value, persistedObservationHash.Value, StringComparison.Ordinal))
        {
            throw new ExoArchiveStatisticsIntegrityViolationException(
                $"O observation_hash persistido para a versão {snapshotVersion.ToString(CultureInfo.InvariantCulture)} do snapshot " +
                $"{phase} de {archive.Value} não corresponde ao hash recomputado a partir dos campos carregados — " +
                "evidência possivelmente adulterada ou corrompida.");
        }

        var recomputedSnapshotHash = ComputeSnapshotHash(snapshotVersion, createdAtUtc, correlation, recomputedObservationHash);
        if (!string.Equals(recomputedSnapshotHash.Value, persistedSnapshotHash.Value, StringComparison.Ordinal))
        {
            throw new ExoArchiveStatisticsIntegrityViolationException(
                $"O snapshot_hash persistido para a versão {snapshotVersion.ToString(CultureInfo.InvariantCulture)} do snapshot " +
                $"{phase} de {archive.Value} não corresponde ao hash recomputado — evidência possivelmente adulterada ou corrompida.");
        }

        return new ExoArchiveStatisticsSnapshot(
            tenant, project, wave, archive, phase, snapshotVersion, archiveStatus, exchangeGuid, archiveGuid,
            itemCount, totalItemSizeBytes, totalDeletedItemSizeBytes, lastLogonTimeUtc, retentionHoldEnabled,
            litigationHoldEnabled, autoExpandingArchiveEnabled, folderCount, foldersSha256, observedAtUtc,
            correlation, createdAtUtc, persistedObservationHash, persistedSnapshotHash);
    }

    /// <summary>
    /// Hash determinístico do CONTEÚDO lógico da observação (exposto para que a camada de persistência
    /// possa resolver convergência idempotente ANTES de conhecer a versão a alocar — mesmo padrão de
    /// <c>DeterministicHash.ComputeBytes</c> em <c>SqlPurviewServiceResultReportStore.PersistAsync</c>).
    /// Inclui <paramref name="observedAtUtc"/> (mesmo princípio de <c>MailboxPrecheckSnapshot.IsSameContentAs</c>) —
    /// nunca inclui <c>SnapshotVersion</c>/<c>CreatedAtUtc</c>/<c>Correlation</c>.
    /// </summary>
    public static Sha256Hash ComputeObservationHash(
        TenantId tenant,
        ProjectId project,
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
        Sha256Hash foldersSha256,
        int folderCount,
        DateTimeOffset observedAtUtc) =>
        DeterministicHash.Compute(
        [
            nameof(ExoArchiveStatisticsSnapshot),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            archive.Value,
            phase.ToString(),
            archiveStatus.ToString(),
            exchangeGuid?.ToString("N") ?? "null",
            archiveGuid?.ToString("N") ?? "null",
            itemCount?.ToString(CultureInfo.InvariantCulture) ?? "null",
            totalItemSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "null",
            totalDeletedItemSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "null",
            lastLogonTimeUtc.HasValue ? TruncateToMilliseconds(lastLogonTimeUtc.Value).UtcTicks.ToString(CultureInfo.InvariantCulture) : "null",
            retentionHoldEnabled?.ToString(CultureInfo.InvariantCulture) ?? "null",
            litigationHoldEnabled?.ToString(CultureInfo.InvariantCulture) ?? "null",
            autoExpandingArchiveEnabled?.ToString(CultureInfo.InvariantCulture) ?? "null",
            foldersSha256.Value,
            folderCount.ToString(CultureInfo.InvariantCulture),
            TruncateToMilliseconds(observedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static Sha256Hash ComputeSnapshotHash(
        int snapshotVersion, DateTimeOffset createdAtUtc, CorrelationId correlation, Sha256Hash observationHash) =>
        DeterministicHash.Compute(
        [
            nameof(ExoArchiveStatisticsSnapshot),
            snapshotVersion.ToString(CultureInfo.InvariantCulture),
            TruncateToMilliseconds(createdAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            correlation.Value.ToString("N"),
            observationHash.Value,
        ]);

    private static void RequireNonNegative(long? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} não pode ser negativo.");
        }
    }

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
