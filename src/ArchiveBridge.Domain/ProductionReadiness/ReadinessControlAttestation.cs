using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Atestação IMUTÁVEL e append-only de UM controle <see cref="ReadinessControlEvidenceSource.Attested"/> do
/// Production Readiness Review (AB-I8-001 escopo item 9) — tenant/project-scoped, tamper-evident e
/// versionado por (tenant, project, controle). Registra a decisão HUMANA explícita de um ator autorizado
/// server-side sobre um controle que ainda não possui evidência automatizada (ex.: "ADR aprovado").
/// <para>
/// Bloqueio estrutural (AB-I8-001 STOP-THE-LINE): <see cref="Create"/> RECUSA qualquer
/// <see cref="ReadinessControlId"/> cuja <see cref="ReadinessControlEvidenceSource"/> no catálogo NÃO seja
/// <see cref="ReadinessControlEvidenceSource.Attested"/> — pen-test, RTO/RPO, SBOM/assinaturas,
/// WDAC/Defender/patching, incident response, hashes/manifests/lineage, backup/restore e as duas
/// invariantes de policy M365 (<see cref="ReadinessControlEvidenceSource.SystemDerived"/>), assim como
/// archive/licença/quota (<see cref="ReadinessControlEvidenceSource.EvidenceUnavailable"/>, AB-I8-003
/// blocker 1) NUNCA podem ser "aprovados" por alegação humana, mesmo por um ator com o papel mais
/// privilegiado.
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="RecordHash"/> a
/// partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record ReadinessControlAttestation
{
    /// <summary>Prefixo versionado do schema deste registro — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.production-readiness.control-attestation.v1";

    private ReadinessControlAttestation(
        TenantId tenant,
        ProjectId project,
        ReadinessControlId controlId,
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
        ControlId = controlId;
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

    /// <summary>Controle atestado — sempre <see cref="ReadinessControlEvidenceSource.Attested"/> no catálogo.</summary>
    public ReadinessControlId ControlId { get; }

    /// <summary>Versão monotônica (1..N) desta atestação dentro de (tenant, project, controle).</summary>
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
    /// <exception cref="ProductionReadinessAttestationNotAllowedException"><paramref name="controlId"/> não é <see cref="ReadinessControlEvidenceSource.Attested"/> no catálogo (SystemDerived/EvidenceUnavailable), ou é desconhecido.</exception>
    /// <exception cref="ArgumentException"><paramref name="submittedBy"/>/<paramref name="submittedByRole"/> vazios, ou o texto tem aparência de segredo/PII.</exception>
    public static ReadinessControlAttestation Create(
        TenantId tenant,
        ProjectId project,
        ReadinessControlId controlId,
        int attestationVersion,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset submittedAtUtc)
    {
        RequireAttestable(controlId);

        if (attestationVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attestationVersion), attestationVersion, "A versão da atestação deve ser positiva.");
        }

        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind == ReadinessEvidenceKind.None)
        {
            throw new ArgumentException("Uma atestação exige uma referência de evidência real (Kind != None).", nameof(evidence));
        }

        var normalizedReasonCode = EvidenceText.RequireSafeOptional(
            reasonCode, nameof(reasonCode), maxLength: 200, p => new ArgumentException($"{p} tem aparência de segredo/PII — recusado.", p));
        var normalizedSubmittedBy = TextValue.Require(submittedBy, nameof(submittedBy), maxLength: 200);
        var normalizedSubmittedByRole = TextValue.Require(submittedByRole, nameof(submittedByRole), maxLength: 50);
        var canonicalSubmittedAt = TruncateToMilliseconds(submittedAtUtc);

        var fingerprint = ComputeContentFingerprint(status, evidence, normalizedReasonCode);
        var hash = ComputeRecordHash(
            tenant, project, controlId, attestationVersion, fingerprint, normalizedSubmittedBy, normalizedSubmittedByRole,
            correlation, canonicalSubmittedAt, CurrentSchemaVersion);

        return new ReadinessControlAttestation(
            tenant, project, controlId, attestationVersion, status, evidence, normalizedReasonCode, normalizedSubmittedBy,
            normalizedSubmittedByRole, correlation, canonicalSubmittedAt, CurrentSchemaVersion, fingerprint, hash);
    }

    /// <summary>Reconstrói uma atestação JÁ PERSISTIDA, revalidando <see cref="ContentFingerprint"/> e <see cref="RecordHash"/> (fail-closed).</summary>
    /// <exception cref="ProductionReadinessIntegrityViolationException">Fingerprint/hash persistidos divergem dos recomputados.</exception>
    public static ReadinessControlAttestation Rehydrate(
        TenantId tenant,
        ProjectId project,
        ReadinessControlId controlId,
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
            throw new ProductionReadinessIntegrityViolationException(
                $"O content_fingerprint persistido para a versão {attestationVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"da atestação de {controlId.Value} não corresponde ao recomputado — registro possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeRecordHash(
            tenant, project, controlId, attestationVersion, persistedContentFingerprint, submittedBy, submittedByRole,
            correlation, submittedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedRecordHash.Value, StringComparison.Ordinal))
        {
            throw new ProductionReadinessIntegrityViolationException(
                $"O record_hash persistido para a versão {attestationVersion.ToString(CultureInfo.InvariantCulture)} da " +
                $"atestação de {controlId.Value} não corresponde ao hash recomputado — registro possivelmente adulterado ou corrompido.");
        }

        return new ReadinessControlAttestation(
            tenant, project, controlId, attestationVersion, status, evidence, reasonCode, submittedBy, submittedByRole,
            correlation, submittedAtUtc, schemaVersion, persistedContentFingerprint, persistedRecordHash);
    }

    /// <summary>
    /// Recusa fail-closed qualquer controle desconhecido ou que não seja <see cref="ReadinessControlEvidenceSource.Attested"/>
    /// (SystemDerived ou EvidenceUnavailable) — a ÚNICA barreira que impede atestação manual de sobrescrever
    /// evidência automatizada, ou de "aprovar" um controle para o qual nenhuma fonte canônica existe.
    /// </summary>
    /// <exception cref="ProductionReadinessAttestationNotAllowedException"><paramref name="controlId"/> desconhecido, SystemDerived ou EvidenceUnavailable.</exception>
    public static void RequireAttestable(ReadinessControlId controlId)
    {
        if (!ReadinessControlCatalog.IsKnown(controlId))
        {
            throw new ProductionReadinessAttestationNotAllowedException(
                $"Controle de readiness desconhecido neste catálogo: {controlId.Value}.");
        }

        var definition = ReadinessControlCatalog.Definition(controlId);
        if (definition.EvidenceSource != ReadinessControlEvidenceSource.Attested)
        {
            var reason = definition.EvidenceSource == ReadinessControlEvidenceSource.EvidenceUnavailable
                ? "nenhuma fonte canônica capaz de comprová-lo existe hoje neste repositório — a ausência de evidência " +
                  "nunca vira um checklist documental aprovável por alegação humana (AB-I8-003 blocker 1)"
                : "é SystemDerived — nunca pode ser aprovado por atestação manual (bloqueio estrutural do work order AB-I8-001)";
            throw new ProductionReadinessAttestationNotAllowedException(
                $"O controle {controlId.Value} {reason}.");
        }
    }

    private static Sha256Hash ComputeContentFingerprint(ReadinessControlStatus status, ReadinessEvidenceReference evidence, string reasonCode) =>
        DeterministicHash.Compute(
        [
            "archivebridge.production-readiness.control-attestation-fingerprint.v1",
            ((int)status).ToString(CultureInfo.InvariantCulture),
            ((int)evidence.Kind).ToString(CultureInfo.InvariantCulture),
            evidence.Fingerprint.Value,
            evidence.Locator,
            reasonCode,
        ]);

    private static Sha256Hash ComputeRecordHash(
        TenantId tenant,
        ProjectId project,
        ReadinessControlId controlId,
        int attestationVersion,
        Sha256Hash contentFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset submittedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(ReadinessControlAttestation),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            controlId.Value,
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
