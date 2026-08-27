using System.Globalization;
using System.Text.RegularExpressions;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Evidência IMUTÁVEL e append-only de UMA build APROVADA de um artifact (AB-I7-008 item 3) —
/// tenant/project-scoped, versionada por (tenant, project, nome do artifact). Identidade determinística
/// verificável por SHA/digest: commit de origem, identidade do builder, instante da build e digest do
/// artifact produzido — nunca uma versão flutuante sem digest/publisher/capability fixados. Usada por
/// <see cref="ArtifactPromotionVerifier"/> para recusar fail-closed qualquer drift entre o que foi
/// aprovado e o que está sendo promovido.
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="RecordHash"/> a
/// partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed partial record BuildProvenanceRecord
{
    /// <summary>Prefixo versionado do schema deste registro.</summary>
    public const string CurrentSchemaVersion = "archivebridge.security.build-provenance-record.v1";

    private const int ArtifactNameMaxLength = 200;
    private const int BuilderIdentityMaxLength = 200;

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();

    private BuildProvenanceRecord(
        TenantId tenant,
        ProjectId project,
        string artifactName,
        int artifactVersion,
        string sourceCommitSha,
        string builderIdentity,
        DateTimeOffset buildTimestampUtc,
        Sha256Hash artifactDigest,
        string approvedBy,
        string approvedByRole,
        CorrelationId correlation,
        DateTimeOffset approvedAtUtc,
        string schemaVersion,
        Sha256Hash contentFingerprint,
        Sha256Hash recordHash)
    {
        Tenant = tenant;
        Project = project;
        ArtifactName = artifactName;
        ArtifactVersion = artifactVersion;
        SourceCommitSha = sourceCommitSha;
        BuilderIdentity = builderIdentity;
        BuildTimestampUtc = buildTimestampUtc;
        ArtifactDigest = artifactDigest;
        ApprovedBy = approvedBy;
        ApprovedByRole = approvedByRole;
        Correlation = correlation;
        ApprovedAtUtc = approvedAtUtc;
        SchemaVersion = schemaVersion;
        ContentFingerprint = contentFingerprint;
        RecordHash = recordHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Nome estável do artifact (ex.: <c>ArchiveBridge.Workers.Upload</c>).</summary>
    public string ArtifactName { get; }

    /// <summary>Versão monotônica (1..N) desta build aprovada dentro de (tenant, project, artifact).</summary>
    public int ArtifactVersion { get; }

    /// <summary>SHA-1 (40 hex minúsculo) do commit de origem.</summary>
    public string SourceCommitSha { get; }

    /// <summary>Identidade do builder que produziu o artifact (ex.: runner de CI), nunca um segredo/token.</summary>
    public string BuilderIdentity { get; }

    /// <summary>Instante REAL em que a build foi produzida.</summary>
    public DateTimeOffset BuildTimestampUtc { get; }

    /// <summary>Digest SHA-256 do artifact produzido — a identidade verificável usada por <see cref="ArtifactPromotionVerifier"/>.</summary>
    public Sha256Hash ArtifactDigest { get; }

    /// <summary>Ator server-side que aprovou esta build.</summary>
    public string ApprovedBy { get; }

    /// <summary>Papel RBAC alegado do ator.</summary>
    public string ApprovedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida.</summary>
    public DateTimeOffset ApprovedAtUtc { get; }

    /// <summary>Versão do schema deste registro.</summary>
    public string SchemaVersion { get; }

    /// <summary>Impressão digital do conteúdo (commit/builder/timestamp/digest) — usada para convergência idempotente.</summary>
    public Sha256Hash ContentFingerprint { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos.</summary>
    public Sha256Hash RecordHash { get; }

    /// <summary>Registra uma nova build aprovada.</summary>
    /// <exception cref="SupplyChainProvenanceInvariantViolationException"><paramref name="sourceCommitSha"/> não é um SHA-1 de 40 hex minúsculo válido.</exception>
    public static BuildProvenanceRecord Approve(
        TenantId tenant,
        ProjectId project,
        string artifactName,
        int artifactVersion,
        string sourceCommitSha,
        string builderIdentity,
        DateTimeOffset buildTimestampUtc,
        Sha256Hash artifactDigest,
        string approvedBy,
        string approvedByRole,
        CorrelationId correlation,
        DateTimeOffset approvedAtUtc)
    {
        if (artifactVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(artifactVersion), artifactVersion, "A versão do artifact deve ser positiva.");
        }

        var normalizedArtifactName = TextValue.Require(artifactName, nameof(artifactName), ArtifactNameMaxLength);
        var normalizedCommitSha = NormalizeCommitSha(sourceCommitSha);
        var normalizedBuilderIdentity = TextValue.Require(builderIdentity, nameof(builderIdentity), BuilderIdentityMaxLength);
        var normalizedApprovedBy = TextValue.Require(approvedBy, nameof(approvedBy), maxLength: 200);
        var normalizedApprovedByRole = TextValue.Require(approvedByRole, nameof(approvedByRole), maxLength: 50);
        var canonicalBuildTimestamp = TruncateToMilliseconds(buildTimestampUtc);
        var canonicalApprovedAt = TruncateToMilliseconds(approvedAtUtc);

        var fingerprint = ComputeContentFingerprint(normalizedCommitSha, normalizedBuilderIdentity, canonicalBuildTimestamp, artifactDigest);
        var hash = ComputeRecordHash(
            tenant, project, normalizedArtifactName, artifactVersion, fingerprint, normalizedApprovedBy,
            normalizedApprovedByRole, correlation, canonicalApprovedAt, CurrentSchemaVersion);

        return new BuildProvenanceRecord(
            tenant, project, normalizedArtifactName, artifactVersion, normalizedCommitSha, normalizedBuilderIdentity,
            canonicalBuildTimestamp, artifactDigest, normalizedApprovedBy, normalizedApprovedByRole, correlation,
            canonicalApprovedAt, CurrentSchemaVersion, fingerprint, hash);
    }

    /// <summary>Reconstrói uma build JÁ PERSISTIDA, revalidando <see cref="ContentFingerprint"/> e <see cref="RecordHash"/> (fail-closed).</summary>
    /// <exception cref="SupplyChainIntegrityViolationException">Fingerprint/hash persistidos divergem dos recomputados.</exception>
    public static BuildProvenanceRecord Rehydrate(
        TenantId tenant,
        ProjectId project,
        string artifactName,
        int artifactVersion,
        string sourceCommitSha,
        string builderIdentity,
        DateTimeOffset buildTimestampUtc,
        Sha256Hash artifactDigest,
        string approvedBy,
        string approvedByRole,
        CorrelationId correlation,
        DateTimeOffset approvedAtUtc,
        string schemaVersion,
        Sha256Hash persistedContentFingerprint,
        Sha256Hash persistedRecordHash)
    {
        var recomputedFingerprint = ComputeContentFingerprint(sourceCommitSha, builderIdentity, buildTimestampUtc, artifactDigest);
        if (!string.Equals(recomputedFingerprint.Value, persistedContentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new SupplyChainIntegrityViolationException(
                $"O content_fingerprint persistido para a versão {artifactVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"da build de {artifactName} não corresponde ao recomputado — registro possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeRecordHash(
            tenant, project, artifactName, artifactVersion, persistedContentFingerprint, approvedBy, approvedByRole,
            correlation, approvedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedRecordHash.Value, StringComparison.Ordinal))
        {
            throw new SupplyChainIntegrityViolationException(
                $"O record_hash persistido para a versão {artifactVersion.ToString(CultureInfo.InvariantCulture)} da " +
                $"build de {artifactName} não corresponde ao hash recomputado — registro possivelmente adulterado ou corrompido.");
        }

        return new BuildProvenanceRecord(
            tenant, project, artifactName, artifactVersion, sourceCommitSha, builderIdentity, buildTimestampUtc,
            artifactDigest, approvedBy, approvedByRole, correlation, approvedAtUtc, schemaVersion,
            persistedContentFingerprint, persistedRecordHash);
    }

    private static string NormalizeCommitSha(string sourceCommitSha)
    {
        if (string.IsNullOrWhiteSpace(sourceCommitSha))
        {
            throw new ArgumentException("sourceCommitSha é obrigatório.", nameof(sourceCommitSha));
        }

        var trimmed = sourceCommitSha.Trim().ToLowerInvariant();
        if (!CommitShaPattern().IsMatch(trimmed))
        {
            throw new SupplyChainProvenanceInvariantViolationException(
                "sourceCommitSha precisa ser um SHA-1 de commit válido (40 caracteres hexadecimais) — nunca uma " +
                "referência flutuante (branch/tag mutável).");
        }

        return trimmed;
    }

    private static Sha256Hash ComputeContentFingerprint(
        string sourceCommitSha, string builderIdentity, DateTimeOffset buildTimestampUtc, Sha256Hash artifactDigest) =>
        DeterministicHash.Compute(
        [
            "archivebridge.security.build-provenance-fingerprint.v1",
            sourceCommitSha,
            builderIdentity,
            TruncateToMilliseconds(buildTimestampUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            artifactDigest.Value,
        ]);

    private static Sha256Hash ComputeRecordHash(
        TenantId tenant,
        ProjectId project,
        string artifactName,
        int artifactVersion,
        Sha256Hash contentFingerprint,
        string approvedBy,
        string approvedByRole,
        CorrelationId correlation,
        DateTimeOffset approvedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(BuildProvenanceRecord),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            artifactName,
            artifactVersion.ToString(CultureInfo.InvariantCulture),
            contentFingerprint.Value,
            approvedBy,
            approvedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(approvedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
