using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>Identidade de um snapshot de precheck de mailbox, gerada pelo servidor.</summary>
public readonly record struct PrecheckSnapshotId(Guid Value)
{
    /// <summary>Gera uma nova identidade.</summary>
    public static PrecheckSnapshotId New() => new(Guid.NewGuid());
}

/// <summary>
/// Snapshot IMUTÁVEL e versionado de UM precheck read-only de tenant/mailbox (runbook §25.2, work order
/// AB-I5-001 item 4): identidade resolvida, status do archive, holds e estatísticas de capacidade quando
/// disponíveis. NUNCA contém assunto/corpo/remetente/destinatário/anexo ou qualquer conteúdo de mailbox —
/// apenas os metadados operacionais estruturados documentados aqui. Nenhuma mutação de mailbox/tenant é
/// representada ou executada por este tipo (somente leitura, work order item 5).
/// <para>
/// A <see cref="Waves.ArchiveRef"/> DEVE ter identidade resolvida (<see cref="ArchiveRef.IsIdentityResolved"/>):
/// um precheck sobre identidade não resolvida seria uma superfície de IDOR (work order item 10) — recusado
/// fail-closed em <see cref="Observe"/>.
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de <c>EvWatermark</c>/<c>InventorySnapshot</c>/
/// <see cref="CapabilityEvidence"/>): <see cref="Rehydrate"/> recomputa <see cref="SnapshotHash"/> a partir
/// dos campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record MailboxPrecheckSnapshot
{
    private const int RecipientTypeDetailsMaxLength = 100;

    private MailboxPrecheckSnapshot(
        PrecheckSnapshotId id,
        TenantId tenant,
        ProjectId project,
        ArchiveRef mailbox,
        int version,
        Guid? exchangeGuid,
        Guid? archiveGuid,
        MailboxArchiveStatus archiveStatus,
        string? recipientTypeDetails,
        bool autoExpandingArchiveEnabled,
        bool litigationHoldEnabled,
        bool retentionHoldEnabled,
        long? archiveItemCount,
        long? archiveTotalSizeBytes,
        long? observedAvailableBytes,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        Sha256Hash snapshotHash)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Mailbox = mailbox;
        Version = version;
        ExchangeGuid = exchangeGuid;
        ArchiveGuid = archiveGuid;
        ArchiveStatus = archiveStatus;
        RecipientTypeDetails = recipientTypeDetails;
        AutoExpandingArchiveEnabled = autoExpandingArchiveEnabled;
        LitigationHoldEnabled = litigationHoldEnabled;
        RetentionHoldEnabled = retentionHoldEnabled;
        ArchiveItemCount = archiveItemCount;
        ArchiveTotalSizeBytes = archiveTotalSizeBytes;
        ObservedAvailableBytes = observedAvailableBytes;
        ObservedAtUtc = observedAtUtc;
        Correlation = correlation;
        RecordedAtUtc = recordedAtUtc;
        SnapshotHash = snapshotHash;
    }

    /// <summary>Registra um novo snapshot a partir de uma observação fresca do adapter substituível.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> não é positivo.</exception>
    /// <exception cref="PurviewValidationException"><paramref name="mailbox"/> não tem identidade resolvida.</exception>
    public static MailboxPrecheckSnapshot Observe(
        PrecheckSnapshotId id,
        TenantId tenant,
        ProjectId project,
        ArchiveRef mailbox,
        int version,
        Guid? exchangeGuid,
        Guid? archiveGuid,
        MailboxArchiveStatus archiveStatus,
        string? recipientTypeDetails,
        bool autoExpandingArchiveEnabled,
        bool litigationHoldEnabled,
        bool retentionHoldEnabled,
        long? archiveItemCount,
        long? archiveTotalSizeBytes,
        long? observedAvailableBytes,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc)
    {
        if (!mailbox.IsIdentityResolved)
        {
            throw new PurviewValidationException(
                "Precheck recusado (fail-closed): a identidade do archive de destino não foi resolvida " +
                "server-side por um manifesto/resolvedor autorizado.");
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "A versão do snapshot deve ser positiva.");
        }

        RequireNonNegative(archiveItemCount, nameof(archiveItemCount));
        RequireNonNegative(archiveTotalSizeBytes, nameof(archiveTotalSizeBytes));
        RequireNonNegative(observedAvailableBytes, nameof(observedAvailableBytes));

        var sanitizedRecipientTypeDetails = string.IsNullOrWhiteSpace(recipientTypeDetails)
            ? null
            : TextValue.Require(recipientTypeDetails, nameof(recipientTypeDetails), RecipientTypeDetailsMaxLength);
        var canonicalObservedAtUtc = TruncateToMilliseconds(observedAtUtc);
        var canonicalRecordedAtUtc = TruncateToMilliseconds(recordedAtUtc);

        var hash = ComputeSnapshotHash(
            tenant, project, mailbox, version, exchangeGuid, archiveGuid, archiveStatus, sanitizedRecipientTypeDetails,
            autoExpandingArchiveEnabled, litigationHoldEnabled, retentionHoldEnabled, archiveItemCount,
            archiveTotalSizeBytes, observedAvailableBytes, canonicalObservedAtUtc, correlation, canonicalRecordedAtUtc);

        return new MailboxPrecheckSnapshot(
            id, tenant, project, mailbox, version, exchangeGuid, archiveGuid, archiveStatus, sanitizedRecipientTypeDetails,
            autoExpandingArchiveEnabled, litigationHoldEnabled, retentionHoldEnabled, archiveItemCount,
            archiveTotalSizeBytes, observedAvailableBytes, canonicalObservedAtUtc, correlation, canonicalRecordedAtUtc, hash);
    }

    /// <summary>Reconstrói um snapshot já persistido, revalidando <see cref="SnapshotHash"/> (fail-closed).</summary>
    /// <exception cref="MailboxPrecheckIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static MailboxPrecheckSnapshot Rehydrate(
        PrecheckSnapshotId id,
        TenantId tenant,
        ProjectId project,
        ArchiveRef mailbox,
        int version,
        Guid? exchangeGuid,
        Guid? archiveGuid,
        MailboxArchiveStatus archiveStatus,
        string? recipientTypeDetails,
        bool autoExpandingArchiveEnabled,
        bool litigationHoldEnabled,
        bool retentionHoldEnabled,
        long? archiveItemCount,
        long? archiveTotalSizeBytes,
        long? observedAvailableBytes,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        Sha256Hash persistedSnapshotHash)
    {
        var recomputed = ComputeSnapshotHash(
            tenant, project, mailbox, version, exchangeGuid, archiveGuid, archiveStatus, recipientTypeDetails,
            autoExpandingArchiveEnabled, litigationHoldEnabled, retentionHoldEnabled, archiveItemCount,
            archiveTotalSizeBytes, observedAvailableBytes, observedAtUtc, correlation, recordedAtUtc);
        if (!string.Equals(recomputed.Value, persistedSnapshotHash.Value, StringComparison.Ordinal))
        {
            throw new MailboxPrecheckIntegrityViolationException(
                $"O snapshot_hash persistido para {id.Value} não corresponde ao hash recomputado a partir dos " +
                "campos carregados — evidência possivelmente adulterada ou corrompida.");
        }

        return new MailboxPrecheckSnapshot(
            id, tenant, project, mailbox, version, exchangeGuid, archiveGuid, archiveStatus, recipientTypeDetails,
            autoExpandingArchiveEnabled, litigationHoldEnabled, retentionHoldEnabled, archiveItemCount,
            archiveTotalSizeBytes, observedAvailableBytes, observedAtUtc, correlation, recordedAtUtc, persistedSnapshotHash);
    }

    /// <summary>
    /// Verdadeiro quando o CONTEÚDO lógico observado é idêntico ao de <paramref name="other"/> — usado
    /// SOMENTE para detectar réplay idempotente (work order item 11); nunca inclui
    /// <see cref="RecordedAtUtc"/>/<see cref="Version"/>/<see cref="Id"/>/<see cref="Correlation"/>.
    /// </summary>
    public bool IsSameContentAs(MailboxPrecheckSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Tenant.Equals(other.Tenant)
            && Project.Equals(other.Project)
            && Mailbox.Identity.Equals(other.Mailbox.Identity)
            && ExchangeGuid == other.ExchangeGuid
            && ArchiveGuid == other.ArchiveGuid
            && ArchiveStatus == other.ArchiveStatus
            && string.Equals(RecipientTypeDetails, other.RecipientTypeDetails, StringComparison.Ordinal)
            && AutoExpandingArchiveEnabled == other.AutoExpandingArchiveEnabled
            && LitigationHoldEnabled == other.LitigationHoldEnabled
            && RetentionHoldEnabled == other.RetentionHoldEnabled
            && ArchiveItemCount == other.ArchiveItemCount
            && ArchiveTotalSizeBytes == other.ArchiveTotalSizeBytes
            && ObservedAvailableBytes == other.ObservedAvailableBytes
            && ObservedAtUtc == other.ObservedAtUtc;
    }

    /// <summary>Identidade do snapshot.</summary>
    public PrecheckSnapshotId Id { get; }

    /// <summary>Tenant do escopo.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo.</summary>
    public ProjectId Project { get; }

    /// <summary>Mailbox/archive de destino com identidade resolvida server-side.</summary>
    public ArchiveRef Mailbox { get; }

    /// <summary>Versão monotônica crescente por (tenant, projeto, archive).</summary>
    public int Version { get; }

    /// <summary>GUID de mailbox observado (quando disponível).</summary>
    public Guid? ExchangeGuid { get; }

    /// <summary>GUID de archive observado (quando disponível).</summary>
    public Guid? ArchiveGuid { get; }

    /// <summary>Status observado do Online Archive — <see cref="MailboxArchiveStatus.Unknown"/> por default fail-closed.</summary>
    public MailboxArchiveStatus ArchiveStatus { get; }

    /// <summary>Tipo de destinatário observado (ex.: <c>UserMailbox</c>).</summary>
    public string? RecipientTypeDetails { get; }

    /// <summary>Verdadeiro quando auto-expanding archive foi observado habilitado — NUNCA eleva o limite principal do adapter.</summary>
    public bool AutoExpandingArchiveEnabled { get; }

    /// <summary>Litigation hold observado.</summary>
    public bool LitigationHoldEnabled { get; }

    /// <summary>Retention hold observado.</summary>
    public bool RetentionHoldEnabled { get; }

    /// <summary>Contagem de itens do archive, quando disponível.</summary>
    public long? ArchiveItemCount { get; }

    /// <summary>Tamanho total usado do archive em bytes, quando disponível.</summary>
    public long? ArchiveTotalSizeBytes { get; }

    /// <summary>Capacidade disponível observada em bytes (estruturada, nunca parseada de string local) — usada no capacity gate.</summary>
    public long? ObservedAvailableBytes { get; }

    /// <summary>Instante em que a observação foi coletada pelo adapter (UTC).</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTE registro foi persistido.</summary>
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash SnapshotHash { get; }

    private static void RequireNonNegative(long? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} não pode ser negativo.");
        }
    }

    private static Sha256Hash ComputeSnapshotHash(
        TenantId tenant, ProjectId project, ArchiveRef mailbox, int version, Guid? exchangeGuid, Guid? archiveGuid,
        MailboxArchiveStatus archiveStatus, string? recipientTypeDetails, bool autoExpandingArchiveEnabled,
        bool litigationHoldEnabled, bool retentionHoldEnabled, long? archiveItemCount, long? archiveTotalSizeBytes,
        long? observedAvailableBytes, DateTimeOffset observedAtUtc, CorrelationId correlation, DateTimeOffset recordedAtUtc) =>
        DeterministicHash.Compute(
        [
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            mailbox.Identity.Value,
            version.ToString(CultureInfo.InvariantCulture),
            exchangeGuid?.ToString("N") ?? string.Empty,
            archiveGuid?.ToString("N") ?? string.Empty,
            archiveStatus.ToString(),
            recipientTypeDetails ?? string.Empty,
            autoExpandingArchiveEnabled.ToString(CultureInfo.InvariantCulture),
            litigationHoldEnabled.ToString(CultureInfo.InvariantCulture),
            retentionHoldEnabled.ToString(CultureInfo.InvariantCulture),
            archiveItemCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            archiveTotalSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            observedAvailableBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            TruncateToMilliseconds(observedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(recordedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
