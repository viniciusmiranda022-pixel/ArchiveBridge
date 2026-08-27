using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Evidência IMUTÁVEL e append-only de UMA versão da policy WDAC/App Control usada pelos workers
/// (AB-I7-008 item 2) — tenant/project-scoped. Identidade/digest/version: <see cref="PolicyDigest"/> é
/// determinístico sobre TODAS as <see cref="Entries"/> (nenhuma allow-all por construção — ver
/// <see cref="WdacAllowlistEntry.Create"/>). Nenhuma policy é aplicada a nenhum host real por este tipo —
/// é um modelo de evidência/validação puro (STOP-THE-LINE do work order).
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="PolicyDigest"/> a
/// partir das entradas REALMENTE carregadas e <see cref="RecordHash"/> a partir de todos os campos
/// REALMENTE carregados, recusando fail-closed qualquer divergência (tampering).
/// </para>
/// </summary>
public sealed record WdacPolicyEvidence
{
    /// <summary>Prefixo versionado do schema deste registro.</summary>
    public const string CurrentSchemaVersion = "archivebridge.security.wdac-policy-evidence.v1";

    private WdacPolicyEvidence(
        TenantId tenant,
        ProjectId project,
        int policyVersion,
        IReadOnlyList<WdacAllowlistEntry> entries,
        Sha256Hash policyDigest,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset issuedAtUtc,
        string schemaVersion,
        Sha256Hash contentFingerprint,
        Sha256Hash recordHash)
    {
        Tenant = tenant;
        Project = project;
        PolicyVersion = policyVersion;
        Entries = entries;
        PolicyDigest = policyDigest;
        IssuedBy = issuedBy;
        IssuedByRole = issuedByRole;
        Correlation = correlation;
        IssuedAtUtc = issuedAtUtc;
        SchemaVersion = schemaVersion;
        ContentFingerprint = contentFingerprint;
        RecordHash = recordHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Versão monotônica (1..N) desta policy dentro de (tenant, project).</summary>
    public int PolicyVersion { get; }

    /// <summary>Entradas da allowlist desta versão — nunca vazia por convenção operacional, mas não exigido estruturalmente (uma allowlist vazia nega tudo, nunca permite tudo).</summary>
    public IReadOnlyList<WdacAllowlistEntry> Entries { get; }

    /// <summary>Digest determinístico de TODAS as <see cref="Entries"/> — detecta tampering das entradas.</summary>
    public Sha256Hash PolicyDigest { get; }

    /// <summary>Ator server-side responsável pela emissão desta versão.</summary>
    public string IssuedBy { get; }

    /// <summary>Papel RBAC alegado do ator.</summary>
    public string IssuedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>Versão do schema deste registro.</summary>
    public string SchemaVersion { get; }

    /// <summary>Impressão digital do conteúdo (apenas <see cref="PolicyDigest"/>) — usada para convergência idempotente; nunca cobre versão/timestamp/ator.</summary>
    public Sha256Hash ContentFingerprint { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos.</summary>
    public Sha256Hash RecordHash { get; }

    /// <summary>Registra uma nova versão da policy (ou converge, via a store, quando o conteúdo é idêntico).</summary>
    public static WdacPolicyEvidence Record(
        TenantId tenant,
        ProjectId project,
        int policyVersion,
        IReadOnlyList<WdacAllowlistEntry> entries,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset issuedAtUtc)
    {
        if (policyVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policyVersion), policyVersion, "A versão da policy deve ser positiva.");
        }

        ArgumentNullException.ThrowIfNull(entries);
        var normalizedEntries = entries.ToArray();
        var normalizedIssuedBy = TextValue.Require(issuedBy, nameof(issuedBy), maxLength: 200);
        var normalizedIssuedByRole = TextValue.Require(issuedByRole, nameof(issuedByRole), maxLength: 50);
        var canonicalIssuedAt = TruncateToMilliseconds(issuedAtUtc);

        var digest = ComputePolicyDigest(normalizedEntries);
        var fingerprint = ComputeContentFingerprint(digest);
        var hash = ComputeRecordHash(
            tenant, project, policyVersion, fingerprint, normalizedIssuedBy, normalizedIssuedByRole, correlation,
            canonicalIssuedAt, CurrentSchemaVersion);

        return new WdacPolicyEvidence(
            tenant, project, policyVersion, normalizedEntries, digest, normalizedIssuedBy, normalizedIssuedByRole,
            correlation, canonicalIssuedAt, CurrentSchemaVersion, fingerprint, hash);
    }

    /// <summary>Reconstrói uma policy JÁ PERSISTIDA, revalidando <see cref="PolicyDigest"/>, <see cref="ContentFingerprint"/> e <see cref="RecordHash"/> (fail-closed).</summary>
    /// <exception cref="WdacPolicyIntegrityViolationException">Digest/fingerprint/hash persistidos divergem dos recomputados a partir das entradas/campos REALMENTE carregados.</exception>
    public static WdacPolicyEvidence Rehydrate(
        TenantId tenant,
        ProjectId project,
        int policyVersion,
        IReadOnlyList<WdacAllowlistEntry> entries,
        Sha256Hash persistedPolicyDigest,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset issuedAtUtc,
        string schemaVersion,
        Sha256Hash persistedContentFingerprint,
        Sha256Hash persistedRecordHash)
    {
        var recomputedDigest = ComputePolicyDigest(entries);
        if (!string.Equals(recomputedDigest.Value, persistedPolicyDigest.Value, StringComparison.Ordinal))
        {
            throw new WdacPolicyIntegrityViolationException(
                $"O policy_digest persistido para a versão {policyVersion.ToString(CultureInfo.InvariantCulture)} não " +
                "corresponde ao digest recomputado a partir das entradas carregadas — policy possivelmente adulterada.");
        }

        var recomputedFingerprint = ComputeContentFingerprint(persistedPolicyDigest);
        if (!string.Equals(recomputedFingerprint.Value, persistedContentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new WdacPolicyIntegrityViolationException(
                $"O content_fingerprint persistido para a versão {policyVersion.ToString(CultureInfo.InvariantCulture)} " +
                "não corresponde ao recomputado — registro possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeRecordHash(
            tenant, project, policyVersion, persistedContentFingerprint, issuedBy, issuedByRole, correlation,
            issuedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedRecordHash.Value, StringComparison.Ordinal))
        {
            throw new WdacPolicyIntegrityViolationException(
                $"O record_hash persistido para a versão {policyVersion.ToString(CultureInfo.InvariantCulture)} não " +
                "corresponde ao hash recomputado — registro possivelmente adulterado ou corrompido.");
        }

        return new WdacPolicyEvidence(
            tenant, project, policyVersion, entries, persistedPolicyDigest, issuedBy, issuedByRole, correlation,
            issuedAtUtc, schemaVersion, persistedContentFingerprint, persistedRecordHash);
    }

    /// <summary>
    /// Valida um binário candidato contra esta policy JÁ REIDRATADA (portanto já revalidada quanto a
    /// tampering). Nunca aplica a policy a nenhum host — apenas decide Allowed/Denied em memória.
    /// </summary>
    public WdacValidationOutcome Validate(WdacCandidateBinary candidate) =>
        Entries.Any(entry => entry.Matches(candidate)) ? WdacValidationOutcome.Allowed : WdacValidationOutcome.Denied;

    /// <summary>Digest determinístico de um conjunto de entradas — exposto para que a store resolva convergência idempotente.</summary>
    public static Sha256Hash ComputePolicyDigest(IReadOnlyList<WdacAllowlistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var parts = new List<string> { "archivebridge.security.wdac-policy.entries.v1" };
        foreach (var entry in entries)
        {
            parts.Add(entry.Publisher ?? string.Empty);
            parts.Add(entry.Sha256?.Value ?? string.Empty);
            parts.Add(entry.PathRule ?? string.Empty);
        }

        return DeterministicHash.Compute(parts);
    }

    private static Sha256Hash ComputeContentFingerprint(Sha256Hash policyDigest) =>
        DeterministicHash.Compute(["archivebridge.security.wdac-policy-fingerprint.v1", policyDigest.Value]);

    private static Sha256Hash ComputeRecordHash(
        TenantId tenant,
        ProjectId project,
        int policyVersion,
        Sha256Hash contentFingerprint,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset issuedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(WdacPolicyEvidence),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            policyVersion.ToString(CultureInfo.InvariantCulture),
            contentFingerprint.Value,
            issuedBy,
            issuedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(issuedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
