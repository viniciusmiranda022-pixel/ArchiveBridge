using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Evidência IMUTÁVEL e append-only de UM exercício de incident-response sintético e não destrutivo
/// (AB-I7-008 item 5) — tenant/project-scoped, versionado por (tenant, project, tipo de drill). Nunca
/// contém segredo/PII: <see cref="EvidenceDigest"/> é sempre um digest SHA-256, nunca o valor observado, e
/// <see cref="Disposition"/> é validada fail-closed contra aparência de segredo/PII
/// (<see cref="SecretRedactor.ContainsSuspectedSecret"/>) antes de aceitar. Nenhum drill produz efeito
/// externo real ou altera dado de cliente (STOP-THE-LINE do work order).
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="RecordHash"/> a
/// partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record IncidentResponseDrillRecord
{
    /// <summary>Prefixo versionado do schema deste registro.</summary>
    public const string CurrentSchemaVersion = "archivebridge.security.incident-response-drill-record.v1";

    private const int DispositionMaxLength = 1000;

    private IncidentResponseDrillRecord(
        TenantId tenant,
        ProjectId project,
        IncidentResponseDrillType drillType,
        int drillVersion,
        IncidentResponseDrillOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        Sha256Hash evidenceDigest,
        string disposition,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        string schemaVersion,
        Sha256Hash contentFingerprint,
        Sha256Hash recordHash)
    {
        Tenant = tenant;
        Project = project;
        DrillType = drillType;
        DrillVersion = drillVersion;
        Outcome = outcome;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        EvidenceDigest = evidenceDigest;
        Disposition = disposition;
        ExecutedBy = executedBy;
        ExecutedByRole = executedByRole;
        Correlation = correlation;
        RecordedAtUtc = recordedAtUtc;
        SchemaVersion = schemaVersion;
        ContentFingerprint = contentFingerprint;
        RecordHash = recordHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Tipo de drill exercitado.</summary>
    public IncidentResponseDrillType DrillType { get; }

    /// <summary>Versão monotônica (1..N) deste drill dentro de (tenant, project, tipo).</summary>
    public int DrillVersion { get; }

    /// <summary>Desfecho REAL observado.</summary>
    public IncidentResponseDrillOutcome Outcome { get; }

    /// <summary>Instante REAL de início do drill.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Instante REAL de conclusão do drill.</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Digest SHA-256 da evidência do drill — NUNCA o segredo/valor observado bruto.</summary>
    public Sha256Hash EvidenceDigest { get; }

    /// <summary>Disposição operacional em texto livre — validada fail-closed contra aparência de segredo/PII.</summary>
    public string Disposition { get; }

    /// <summary>Ator server-side responsável pela execução do drill.</summary>
    public string ExecutedBy { get; }

    /// <summary>Papel RBAC alegado do ator.</summary>
    public string ExecutedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida.</summary>
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>Versão do schema deste registro.</summary>
    public string SchemaVersion { get; }

    /// <summary>Impressão digital do conteúdo (desfecho/timestamps/evidência/disposition) — usada para convergência idempotente.</summary>
    public Sha256Hash ContentFingerprint { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos.</summary>
    public Sha256Hash RecordHash { get; }

    /// <summary>Registra um drill REALMENTE executado.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completedAtUtc"/> é anterior a <paramref name="startedAtUtc"/>, ou <paramref name="drillVersion"/> não é positivo.</exception>
    /// <exception cref="IncidentResponseInvariantViolationException"><paramref name="disposition"/> aparenta conter segredo/PII.</exception>
    public static IncidentResponseDrillRecord Record(
        TenantId tenant,
        ProjectId project,
        IncidentResponseDrillType drillType,
        int drillVersion,
        IncidentResponseDrillOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        Sha256Hash evidenceDigest,
        string disposition,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc)
    {
        if (drillVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(drillVersion), drillVersion, "A versão do drill deve ser positiva.");
        }

        if (completedAtUtc < startedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAtUtc), completedAtUtc, "O fim do drill não pode ser anterior ao início.");
        }

        var normalizedDisposition = EvidenceText.RequireSafe(
            disposition, nameof(disposition), DispositionMaxLength, m => new IncidentResponseInvariantViolationException(
                $"{m} aparenta conter um segredo/PII (SAS/token/cookie/e-mail/caminho UNC) — recusado por design (fail-closed)."));
        var normalizedExecutedBy = TextValue.Require(executedBy, nameof(executedBy), maxLength: 200);
        var normalizedExecutedByRole = TextValue.Require(executedByRole, nameof(executedByRole), maxLength: 50);
        var canonicalStartedAt = TruncateToMilliseconds(startedAtUtc);
        var canonicalCompletedAt = TruncateToMilliseconds(completedAtUtc);
        var canonicalRecordedAt = TruncateToMilliseconds(recordedAtUtc);

        var fingerprint = ComputeContentFingerprint(outcome, canonicalStartedAt, canonicalCompletedAt, evidenceDigest, normalizedDisposition);
        var hash = ComputeRecordHash(
            tenant, project, drillType, drillVersion, fingerprint, normalizedExecutedBy, normalizedExecutedByRole,
            correlation, canonicalRecordedAt, CurrentSchemaVersion);

        return new IncidentResponseDrillRecord(
            tenant, project, drillType, drillVersion, outcome, canonicalStartedAt, canonicalCompletedAt, evidenceDigest,
            normalizedDisposition, normalizedExecutedBy, normalizedExecutedByRole, correlation, canonicalRecordedAt,
            CurrentSchemaVersion, fingerprint, hash);
    }

    /// <summary>Reconstrói um drill JÁ PERSISTIDO, revalidando <see cref="ContentFingerprint"/> e <see cref="RecordHash"/> (fail-closed).</summary>
    /// <exception cref="IncidentResponseIntegrityViolationException">Fingerprint/hash persistidos divergem dos recomputados.</exception>
    public static IncidentResponseDrillRecord Rehydrate(
        TenantId tenant,
        ProjectId project,
        IncidentResponseDrillType drillType,
        int drillVersion,
        IncidentResponseDrillOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        Sha256Hash evidenceDigest,
        string disposition,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        string schemaVersion,
        Sha256Hash persistedContentFingerprint,
        Sha256Hash persistedRecordHash)
    {
        var recomputedFingerprint = ComputeContentFingerprint(outcome, startedAtUtc, completedAtUtc, evidenceDigest, disposition);
        if (!string.Equals(recomputedFingerprint.Value, persistedContentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new IncidentResponseIntegrityViolationException(
                $"O content_fingerprint persistido para a versão {drillVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"do drill {drillType} não corresponde ao recomputado — registro possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeRecordHash(
            tenant, project, drillType, drillVersion, persistedContentFingerprint, executedBy, executedByRole,
            correlation, recordedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedRecordHash.Value, StringComparison.Ordinal))
        {
            throw new IncidentResponseIntegrityViolationException(
                $"O record_hash persistido para a versão {drillVersion.ToString(CultureInfo.InvariantCulture)} do " +
                $"drill {drillType} não corresponde ao hash recomputado — registro possivelmente adulterado ou corrompido.");
        }

        return new IncidentResponseDrillRecord(
            tenant, project, drillType, drillVersion, outcome, startedAtUtc, completedAtUtc, evidenceDigest,
            disposition, executedBy, executedByRole, correlation, recordedAtUtc, schemaVersion,
            persistedContentFingerprint, persistedRecordHash);
    }

    private static Sha256Hash ComputeContentFingerprint(
        IncidentResponseDrillOutcome outcome, DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc,
        Sha256Hash evidenceDigest, string disposition) =>
        DeterministicHash.Compute(
        [
            "archivebridge.security.incident-response-drill-fingerprint.v1",
            ((int)outcome).ToString(CultureInfo.InvariantCulture),
            TruncateToMilliseconds(startedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            TruncateToMilliseconds(completedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            evidenceDigest.Value,
            disposition,
        ]);

    private static Sha256Hash ComputeRecordHash(
        TenantId tenant,
        ProjectId project,
        IncidentResponseDrillType drillType,
        int drillVersion,
        Sha256Hash contentFingerprint,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset recordedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(IncidentResponseDrillRecord),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            ((int)drillType).ToString(CultureInfo.InvariantCulture),
            drillVersion.ToString(CultureInfo.InvariantCulture),
            contentFingerprint.Value,
            executedBy,
            executedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(recordedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
