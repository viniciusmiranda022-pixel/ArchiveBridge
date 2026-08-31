using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Atestação IMUTÁVEL e append-only de UM critério <see cref="MigrationCompletionCriterionEvidenceSource.Attested"/>
/// de encerramento de migração (AB-I8-010 escopo obrigatório item 7/8) — tenant/project-scoped, tamper-evident
/// e versionado por (tenant, project, critério). Registra a decisão HUMANA explícita de um ator autorizado
/// server-side sobre um critério que ainda não possui evidência automatizada (mesmo princípio de
/// <see cref="ReadinessControlAttestation"/>, Passo 1).
/// <para>
/// Bloqueio estrutural (STOP-THE-LINE): <see cref="Create"/> RECUSA qualquer <see cref="MigrationCompletionCriterionId"/>
/// cuja <see cref="MigrationCompletionCriterionEvidenceSource"/> no catálogo NÃO seja
/// <see cref="MigrationCompletionCriterionEvidenceSource.Attested"/> — reconciliação fechada e coleta de
/// resultados do provider NUNCA podem ser "aprovados" por alegação humana, mesmo por um ator com o papel mais
/// privilegiado.
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="RecordHash"/> a
/// partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record MigrationCompletionCriterionAttestation
{
    /// <summary>Prefixo versionado do schema deste registro — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.migration-completion.criterion-attestation.v1";

    private MigrationCompletionCriterionAttestation(
        TenantId tenant,
        ProjectId project,
        MigrationCompletionCriterionId criterionId,
        int attestationVersion,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset submittedAtUtc,
        string schemaVersion,
        Sha256Hash contentFingerprint,
        Sha256Hash recordHash)
    {
        Tenant = tenant;
        Project = project;
        CriterionId = criterionId;
        AttestationVersion = attestationVersion;
        Status = status;
        Evidence = evidence;
        ReasonCode = reasonCode;
        SubmittedBy = submittedBy;
        SubmittedByRole = submittedByRole;
        Correlation = correlation;
        SubmittedAtUtc = submittedAtUtc;
        SchemaVersion = schemaVersion;
        ContentFingerprint = contentFingerprint;
        RecordHash = recordHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Critério atestado — sempre <see cref="MigrationCompletionCriterionEvidenceSource.Attested"/> no catálogo.</summary>
    public MigrationCompletionCriterionId CriterionId { get; }

    /// <summary>Versão monotônica (1..N) desta atestação dentro de (tenant, project, critério).</summary>
    public int AttestationVersion { get; }

    /// <summary>Status atestado pelo ator.</summary>
    public ReadinessControlStatus Status { get; }

    /// <summary>Referência opaca à evidência que sustenta a atestação — nunca conteúdo bruto/segredo/PII.</summary>
    public ReadinessEvidenceReference Evidence { get; }

    /// <summary>Código curto e sanitizado explicando a atestação.</summary>
    public string ReasonCode { get; }

    /// <summary>Ator server-side responsável pela atestação (nunca anônimo, nunca alegado pelo payload).</summary>
    public string SubmittedBy { get; }

    /// <summary>Papel RBAC do ator no instante da atestação.</summary>
    public string SubmittedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset SubmittedAtUtc { get; }

    /// <summary>Versão do schema deste registro.</summary>
    public string SchemaVersion { get; }

    /// <summary>Impressão digital do conteúdo (status/evidência/motivo) — usada para convergência idempotente.</summary>
    public Sha256Hash ContentFingerprint { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos.</summary>
    public Sha256Hash RecordHash { get; }

    /// <summary>Cria uma nova atestação.</summary>
    /// <exception cref="MigrationCompletionAttestationNotAllowedException"><paramref name="criterionId"/> não é Attested no catálogo (SystemDerived), ou é desconhecido.</exception>
    /// <exception cref="ArgumentException"><paramref name="submittedBy"/>/<paramref name="submittedByRole"/> vazios, ou o texto tem aparência de segredo/PII, ou <paramref name="status"/> é Pass sem evidência real.</exception>
    public static MigrationCompletionCriterionAttestation Create(
        TenantId tenant,
        ProjectId project,
        MigrationCompletionCriterionId criterionId,
        int attestationVersion,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset submittedAtUtc)
    {
        RequireAttestable(criterionId);

        if (attestationVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attestationVersion), attestationVersion, "A versão da atestação deve ser positiva.");
        }

        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind == ReadinessEvidenceKind.None)
        {
            throw new ArgumentException("Uma atestação exige uma referência de evidência real (Kind != None) — ausência de evidência nunca vira aprovação implícita.", nameof(evidence));
        }

        var normalizedReasonCode = EvidenceText.RequireSafeOptional(
            reasonCode, nameof(reasonCode), maxLength: 200, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p));
        var normalizedSubmittedBy = TextValue.Require(submittedBy, nameof(submittedBy), maxLength: 200);
        var normalizedSubmittedByRole = TextValue.Require(submittedByRole, nameof(submittedByRole), maxLength: 50);
        var canonicalSubmittedAt = TruncateToMilliseconds(submittedAtUtc);

        var fingerprint = ComputeContentFingerprint(status, evidence, normalizedReasonCode);
        var hash = ComputeRecordHash(
            tenant, project, criterionId, attestationVersion, fingerprint, normalizedSubmittedBy, normalizedSubmittedByRole,
            correlation, canonicalSubmittedAt, CurrentSchemaVersion);

        return new MigrationCompletionCriterionAttestation(
            tenant, project, criterionId, attestationVersion, status, evidence, normalizedReasonCode, normalizedSubmittedBy,
            normalizedSubmittedByRole, correlation, canonicalSubmittedAt, CurrentSchemaVersion, fingerprint, hash);
    }

    /// <summary>Reconstrói uma atestação JÁ PERSISTIDA, revalidando <see cref="ContentFingerprint"/> e <see cref="RecordHash"/> (fail-closed).</summary>
    /// <exception cref="MigrationCompletionIntegrityViolationException">Fingerprint/hash persistidos divergem dos recomputados.</exception>
    public static MigrationCompletionCriterionAttestation Rehydrate(
        TenantId tenant,
        ProjectId project,
        MigrationCompletionCriterionId criterionId,
        int attestationVersion,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset submittedAtUtc,
        string schemaVersion,
        Sha256Hash persistedContentFingerprint,
        Sha256Hash persistedRecordHash)
    {
        var recomputedFingerprint = ComputeContentFingerprint(status, evidence, reasonCode);
        if (!string.Equals(recomputedFingerprint.Value, persistedContentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new MigrationCompletionIntegrityViolationException(
                $"O content_fingerprint persistido para a versão {attestationVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"da atestação de {criterionId.Value} não corresponde ao recomputado — registro possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeRecordHash(
            tenant, project, criterionId, attestationVersion, persistedContentFingerprint, submittedBy, submittedByRole,
            correlation, submittedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedRecordHash.Value, StringComparison.Ordinal))
        {
            throw new MigrationCompletionIntegrityViolationException(
                $"O record_hash persistido para a versão {attestationVersion.ToString(CultureInfo.InvariantCulture)} da " +
                $"atestação de {criterionId.Value} não corresponde ao hash recomputado — registro possivelmente adulterado ou corrompido.");
        }

        return new MigrationCompletionCriterionAttestation(
            tenant, project, criterionId, attestationVersion, status, evidence, reasonCode, submittedBy, submittedByRole,
            correlation, submittedAtUtc, schemaVersion, persistedContentFingerprint, persistedRecordHash);
    }

    /// <summary>
    /// Recusa fail-closed qualquer critério desconhecido ou que não seja
    /// <see cref="MigrationCompletionCriterionEvidenceSource.Attested"/> (SystemDerived) — a ÚNICA barreira que
    /// impede atestação manual de sobrescrever evidência automatizada.
    /// </summary>
    /// <exception cref="MigrationCompletionAttestationNotAllowedException"><paramref name="criterionId"/> desconhecido ou SystemDerived.</exception>
    public static void RequireAttestable(MigrationCompletionCriterionId criterionId)
    {
        if (!MigrationCompletionCriterionCatalog.IsKnown(criterionId))
        {
            throw new MigrationCompletionAttestationNotAllowedException(
                $"Critério de encerramento desconhecido neste catálogo: {criterionId.Value}.");
        }

        var definition = MigrationCompletionCriterionCatalog.Definition(criterionId);
        if (definition.EvidenceSource != MigrationCompletionCriterionEvidenceSource.Attested)
        {
            throw new MigrationCompletionAttestationNotAllowedException(
                $"O critério {criterionId.Value} é SystemDerived — nunca pode ser aprovado por atestação manual (bloqueio estrutural).");
        }
    }

    private static Sha256Hash ComputeContentFingerprint(ReadinessControlStatus status, ReadinessEvidenceReference evidence, string reasonCode) =>
        DeterministicHash.Compute(
        [
            "archivebridge.migration-completion.criterion-attestation-fingerprint.v1",
            ((int)status).ToString(CultureInfo.InvariantCulture),
            ((int)evidence.Kind).ToString(CultureInfo.InvariantCulture),
            evidence.Fingerprint.Value,
            evidence.Locator,
            reasonCode,
        ]);

    private static Sha256Hash ComputeRecordHash(
        TenantId tenant,
        ProjectId project,
        MigrationCompletionCriterionId criterionId,
        int attestationVersion,
        Sha256Hash contentFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset submittedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(MigrationCompletionCriterionAttestation),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            criterionId.Value,
            attestationVersion.ToString(CultureInfo.InvariantCulture),
            contentFingerprint.Value,
            submittedBy,
            submittedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(submittedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
