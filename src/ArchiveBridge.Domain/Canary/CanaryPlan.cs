using System.Globalization;
using System.Text.RegularExpressions;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Plano IMUTÁVEL e append-only de UMA versão do canário de produção (AB-I8-004, escopo obrigatório item 1)
/// — vincula explicitamente o Production Readiness Review canônico usado como gate de entrada (versão +
/// <see cref="ReadinessReviewFingerprint"/>) e o build/digest/policy/capability EXATOS sob canário. NUNCA
/// marca <c>ProductionReady</c>/<c>GoLive</c>/projeto <c>COMPLETED</c>, NUNCA inicia canário real, NUNCA
/// escreve em Purview/EXO/Graph/EV/AzCopy/host real (STOP-THE-LINE).
/// <para>
/// Identidade opaca (<see cref="PlanId"/>) estável ao longo de todas as versões de um mesmo plano —
/// versionamento monotônico por (tenant, project): a MESMA "impressão digital de vinculação"
/// (<see cref="PlanFingerprint"/>) converge para a MESMA <see cref="PlanVersion"/> (replay idempotente); uma
/// mudança REAL no review vinculado, no build/commit revisado, na policy version ou na capability matrix
/// produz uma versão nova — nunca sobrescreve uma anterior (escopo obrigatório item 5: same-build promotion
/// invariant / drift invalida a versão anterior).
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="PlanFingerprint"/>
/// e <see cref="PlanHash"/> a partir dos campos REALMENTE carregados, recusando fail-closed qualquer
/// divergência.
/// </para>
/// </summary>
public sealed partial record CanaryPlan
{
    /// <summary>Prefixo versionado do schema deste plano — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.canary.plan.v1";

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();

    private CanaryPlan(
        TenantId tenant,
        ProjectId project,
        CanaryPlanId planId,
        int planVersion,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        Sha256Hash planFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc,
        string schemaVersion,
        Sha256Hash planHash)
    {
        Tenant = tenant;
        Project = project;
        PlanId = planId;
        PlanVersion = planVersion;
        ReadinessReviewVersion = readinessReviewVersion;
        ReadinessReviewFingerprint = readinessReviewFingerprint;
        BuildCommitSha = buildCommitSha;
        BuildArtifactDigest = buildArtifactDigest;
        PolicyVersionFingerprint = policyVersionFingerprint;
        CapabilityMatrixFingerprint = capabilityMatrixFingerprint;
        PlanFingerprint = planFingerprint;
        AuthorizedBy = authorizedBy;
        AuthorizedByRole = authorizedByRole;
        Correlation = correlation;
        AuthorizedAtUtc = authorizedAtUtc;
        SchemaVersion = schemaVersion;
        PlanHash = planHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Identidade opaca do plano — estável ao longo de todas as suas versões.</summary>
    public CanaryPlanId PlanId { get; }

    /// <summary>Versão monotônica (1..N) deste plano dentro de (tenant, project).</summary>
    public int PlanVersion { get; }

    /// <summary><see cref="ProductionReadinessReviewSnapshot.ReviewVersion"/> do Production Readiness Review canônico usado como gate de entrada.</summary>
    public int ReadinessReviewVersion { get; }

    /// <summary><see cref="ProductionReadinessReviewSnapshot.ReviewFingerprint"/> do review vinculado — vínculo explícito exigido pelo escopo obrigatório item 1.</summary>
    public Sha256Hash ReadinessReviewFingerprint { get; }

    /// <summary>SHA-1 (40 hex minúsculo) do commit sob canário — herdado do build EXATO já revisado pelo Production Readiness Review, nunca fornecido pelo chamador.</summary>
    public string BuildCommitSha { get; }

    /// <summary>Digest do artifact/build sob canário — mesmo build/digest promovível (escopo obrigatório item 5), nunca um fork "canário" separado de "produção".</summary>
    public Sha256Hash BuildArtifactDigest { get; }

    /// <summary>Fingerprint opaco da policy version efetiva no instante da autorização — herdado do review vinculado.</summary>
    public Sha256Hash PolicyVersionFingerprint { get; }

    /// <summary>Fingerprint opaco da capability matrix efetiva no instante da autorização — herdado do review vinculado.</summary>
    public Sha256Hash CapabilityMatrixFingerprint { get; }

    /// <summary>
    /// Impressão digital determinística do CONJUNTO DE VINCULAÇÃO usado para compor este plano — chave de
    /// convergência idempotente (replay idêntico nunca duplica versão); qualquer mudança real no review
    /// vinculado, no build/commit, na policy version ou na capability matrix produz uma versão nova.
    /// </summary>
    public Sha256Hash PlanFingerprint { get; }

    /// <summary>Ator server-side responsável pela autorização (nunca anônimo, nunca alegado pelo payload).</summary>
    public string AuthorizedBy { get; }

    /// <summary>Papel RBAC do ator no instante da autorização.</summary>
    public string AuthorizedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi autorizada (append-only — nunca mutado depois).</summary>
    public DateTimeOffset AuthorizedAtUtc { get; }

    /// <summary>Versão do schema deste plano.</summary>
    public string SchemaVersion { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos — recomputado e validado fail-closed em toda leitura.</summary>
    public Sha256Hash PlanHash { get; }

    /// <summary>
    /// Compõe um novo plano de canário — SOMENTE quando <paramref name="readinessOutcome"/> é
    /// <see cref="ProductionReadinessOutcome.ReadyForCanary"/> (defesa em profundidade: o gate de entrada
    /// primário já deve ter sido verificado pela Application layer ANTES de chamar este método; este
    /// construtor recusa fail-closed qualquer outro valor, mesmo padrão "impossível por construção" de
    /// <see cref="ProductionReadinessReviewSnapshot.Compose"/>).
    /// </summary>
    /// <exception cref="CanaryEntryGateBlockedException"><paramref name="readinessOutcome"/> não é <see cref="ProductionReadinessOutcome.ReadyForCanary"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="buildCommitSha"/> não é um SHA-1 válido, ou <paramref name="authorizedBy"/>/<paramref name="authorizedByRole"/> vazios.</exception>
    public static CanaryPlan Compose(
        TenantId tenant,
        ProjectId project,
        CanaryPlanId planId,
        int planVersion,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        ProductionReadinessOutcome readinessOutcome,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc)
    {
        if (readinessOutcome != ProductionReadinessOutcome.ReadyForCanary)
        {
            throw new CanaryEntryGateBlockedException(
                "Um plano de canário não pode ser autorizado sem um Production Readiness Review canônico e " +
                "vigente com desfecho ReadyForCanary (fail-closed).");
        }

        if (planVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planVersion), planVersion, "A versão do plano deve ser positiva.");
        }

        var normalizedCommitSha = NormalizeCommitSha(buildCommitSha);
        var normalizedAuthorizedBy = TextValue.Require(authorizedBy, nameof(authorizedBy), maxLength: 200);
        var normalizedAuthorizedByRole = TextValue.Require(authorizedByRole, nameof(authorizedByRole), maxLength: 50);
        var canonicalAuthorizedAt = TruncateToMilliseconds(authorizedAtUtc);

        var planFingerprint = ComputePlanFingerprint(
            readinessReviewVersion, readinessReviewFingerprint, normalizedCommitSha, buildArtifactDigest,
            policyVersionFingerprint, capabilityMatrixFingerprint);
        var hash = ComputePlanHash(
            tenant, project, planId, planVersion, readinessReviewVersion, readinessReviewFingerprint, normalizedCommitSha,
            buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, planFingerprint, normalizedAuthorizedBy,
            normalizedAuthorizedByRole, correlation, canonicalAuthorizedAt, CurrentSchemaVersion);

        return new CanaryPlan(
            tenant, project, planId, planVersion, readinessReviewVersion, readinessReviewFingerprint, normalizedCommitSha,
            buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, planFingerprint, normalizedAuthorizedBy,
            normalizedAuthorizedByRole, correlation, canonicalAuthorizedAt, CurrentSchemaVersion, hash);
    }

    /// <summary>
    /// Reconstrói um plano JÁ PERSISTIDO (uso exclusivo da camada de persistência), revalidando
    /// <see cref="PlanFingerprint"/> e <see cref="PlanHash"/> contra os campos REALMENTE carregados.
    /// </summary>
    /// <exception cref="CanaryIntegrityViolationException">O fingerprint/hash persistido diverge do recomputado.</exception>
    public static CanaryPlan Rehydrate(
        TenantId tenant,
        ProjectId project,
        CanaryPlanId planId,
        int planVersion,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        Sha256Hash persistedPlanFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc,
        string schemaVersion,
        Sha256Hash persistedPlanHash)
    {
        var recomputedPlanFingerprint = ComputePlanFingerprint(
            readinessReviewVersion, readinessReviewFingerprint, buildCommitSha, buildArtifactDigest,
            policyVersionFingerprint, capabilityMatrixFingerprint);
        if (!string.Equals(recomputedPlanFingerprint.Value, persistedPlanFingerprint.Value, StringComparison.Ordinal))
        {
            throw new CanaryIntegrityViolationException(
                $"O plan_fingerprint persistido para a versão {planVersion.ToString(CultureInfo.InvariantCulture)} do " +
                "plano de canário não corresponde ao fingerprint recomputado a partir dos campos carregados — " +
                "possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputePlanHash(
            tenant, project, planId, planVersion, readinessReviewVersion, readinessReviewFingerprint, buildCommitSha,
            buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, persistedPlanFingerprint, authorizedBy,
            authorizedByRole, correlation, authorizedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedPlanHash.Value, StringComparison.Ordinal))
        {
            throw new CanaryIntegrityViolationException(
                $"O plan_hash persistido para a versão {planVersion.ToString(CultureInfo.InvariantCulture)} do plano " +
                "de canário não corresponde ao hash recomputado — possivelmente adulterado ou corrompido.");
        }

        return new CanaryPlan(
            tenant, project, planId, planVersion, readinessReviewVersion, readinessReviewFingerprint, buildCommitSha,
            buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, persistedPlanFingerprint, authorizedBy,
            authorizedByRole, correlation, authorizedAtUtc, schemaVersion, persistedPlanHash);
    }

    /// <summary>
    /// Impressão digital determinística do conjunto de vinculação, exposta para que a camada de persistência
    /// resolva convergência idempotente ANTES de conhecer a versão a alocar (mesmo padrão de
    /// <see cref="ProductionReadinessReviewSnapshot.ComputeReviewFingerprint"/>). NUNCA cobre plano/versão/
    /// timestamp/ator (para que uma autorização concorrente idêntica convirja para a MESMA versão).
    /// </summary>
    public static Sha256Hash ComputePlanFingerprint(
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint) =>
        DeterministicHash.Compute(
        [
            "archivebridge.canary.plan-fingerprint.v1",
            CanaryScenarioCatalog.CurrentCatalogVersion,
            readinessReviewVersion.ToString(CultureInfo.InvariantCulture),
            readinessReviewFingerprint.Value,
            buildCommitSha,
            buildArtifactDigest.Value,
            policyVersionFingerprint.Value,
            capabilityMatrixFingerprint.Value,
        ]);

    private static Sha256Hash ComputePlanHash(
        TenantId tenant,
        ProjectId project,
        CanaryPlanId planId,
        int planVersion,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        Sha256Hash planFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(CanaryPlan),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            planId.Value.ToString("N"),
            planVersion.ToString(CultureInfo.InvariantCulture),
            readinessReviewVersion.ToString(CultureInfo.InvariantCulture),
            readinessReviewFingerprint.Value,
            buildCommitSha,
            buildArtifactDigest.Value,
            policyVersionFingerprint.Value,
            capabilityMatrixFingerprint.Value,
            planFingerprint.Value,
            authorizedBy,
            authorizedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(authorizedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static string NormalizeCommitSha(string buildCommitSha)
    {
        if (string.IsNullOrWhiteSpace(buildCommitSha))
        {
            throw new ArgumentException("buildCommitSha é obrigatório.", nameof(buildCommitSha));
        }

        var trimmed = buildCommitSha.Trim().ToLowerInvariant();
        if (!CommitShaPattern().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "buildCommitSha precisa ser um SHA-1 de commit válido (40 caracteres hexadecimais) — nunca uma " +
                "referência flutuante (branch/tag mutável).",
                nameof(buildCommitSha));
        }

        return trimmed;
    }

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
