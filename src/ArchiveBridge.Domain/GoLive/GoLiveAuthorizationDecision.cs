using System.Globalization;
using System.Text.RegularExpressions;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.GoLive;

/// <summary>
/// Decisão IMUTÁVEL e append-only de UMA versão da autorização de go-live/primeira onda real (AB-I8-010,
/// escopo obrigatório item 1) — vincula explicitamente o <see cref="CanaryPlanId"/>/versão/fingerprint do
/// canário canônico julgado, o review de Production Readiness canônico (versão + fingerprint) usado como gate
/// de entrada, e o build/commit SHA/artifact digest/policy version/capability fingerprint EXATOS herdados do
/// canário (nunca recomputados nem aceitos do caller — escopo obrigatório item 3: same-build/same-policy
/// promotion invariant). NUNCA marca migração/projeto/wave <c>Completed</c> (ver
/// <see cref="GoLiveOutcome"/>), NUNCA inicia efeito real em Purview/EXO/Graph/EV/AzCopy/host/tenant M365
/// (STOP-THE-LINE).
/// <para>
/// Identidade opaca (<see cref="AuthorizationId"/>) estável ao longo de todas as versões de uma mesma decisão
/// — versionamento monotônico por (tenant, project): a MESMA "impressão digital de julgamento"
/// (<see cref="AuthorizationFingerprint"/>) converge para a MESMA <see cref="AuthorizationVersion"/> (replay
/// idempotente); uma mudança REAL em qualquer dependência (canário, review vigente, ou qualquer controle
/// operacional/M365 revalidado fresco) produz uma versão nova — nunca sobrescreve uma anterior.
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa
/// <see cref="AuthorizationFingerprint"/>/<see cref="AuthorizationHash"/> a partir dos campos REALMENTE
/// carregados e RE-EXECUTA o avaliador puro sobre os <see cref="OperationalControlResults"/> carregados,
/// recusando fail-closed qualquer divergência entre o <see cref="Outcome"/> persistido e o recomputado.
/// </para>
/// </summary>
public sealed partial record GoLiveAuthorizationDecision
{
    /// <summary>Prefixo versionado do schema desta decisão — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.golive.authorization.v1";

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();

    private GoLiveAuthorizationDecision(
        TenantId tenant,
        ProjectId project,
        GoLiveAuthorizationId authorizationId,
        int authorizationVersion,
        CanaryPlanId canaryPlanId,
        int canaryPlanVersion,
        Sha256Hash canaryPlanFingerprint,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        CanaryOutcome canaryOutcomeAtAuthorization,
        int? currentReadinessReviewVersionAtAuthorization,
        Sha256Hash? currentReadinessReviewFingerprintAtAuthorization,
        IReadOnlyList<ReadinessControlResult> operationalControlResults,
        GoLiveOutcome outcome,
        IReadOnlyList<GoLiveBlocker> blockers,
        Sha256Hash authorizationFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc,
        string schemaVersion,
        Sha256Hash authorizationHash)
    {
        Tenant = tenant;
        Project = project;
        AuthorizationId = authorizationId;
        AuthorizationVersion = authorizationVersion;
        CanaryPlanId = canaryPlanId;
        CanaryPlanVersion = canaryPlanVersion;
        CanaryPlanFingerprint = canaryPlanFingerprint;
        ReadinessReviewVersion = readinessReviewVersion;
        ReadinessReviewFingerprint = readinessReviewFingerprint;
        BuildCommitSha = buildCommitSha;
        BuildArtifactDigest = buildArtifactDigest;
        PolicyVersionFingerprint = policyVersionFingerprint;
        CapabilityMatrixFingerprint = capabilityMatrixFingerprint;
        CanaryOutcomeAtAuthorization = canaryOutcomeAtAuthorization;
        CurrentReadinessReviewVersionAtAuthorization = currentReadinessReviewVersionAtAuthorization;
        CurrentReadinessReviewFingerprintAtAuthorization = currentReadinessReviewFingerprintAtAuthorization;
        OperationalControlResults = operationalControlResults;
        Outcome = outcome;
        Blockers = blockers;
        AuthorizationFingerprint = authorizationFingerprint;
        AuthorizedBy = authorizedBy;
        AuthorizedByRole = authorizedByRole;
        Correlation = correlation;
        AuthorizedAtUtc = authorizedAtUtc;
        SchemaVersion = schemaVersion;
        AuthorizationHash = authorizationHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Identidade opaca da decisão — estável ao longo de todas as suas versões.</summary>
    public GoLiveAuthorizationId AuthorizationId { get; }

    /// <summary>Versão monotônica (1..N) desta decisão dentro de (tenant, project).</summary>
    public int AuthorizationVersion { get; }

    /// <summary>Identidade opaca do plano de canário julgado — vínculo explícito exigido pelo escopo obrigatório item 1.</summary>
    public CanaryPlanId CanaryPlanId { get; }

    /// <summary><see cref="CanaryPlan.PlanVersion"/> do canário canônico julgado.</summary>
    public int CanaryPlanVersion { get; }

    /// <summary><see cref="CanaryPlan.PlanFingerprint"/> do canário vinculado.</summary>
    public Sha256Hash CanaryPlanFingerprint { get; }

    /// <summary><see cref="ProductionReadinessReviewSnapshot.ReviewVersion"/> herdado do canário (nunca fornecido pelo chamador).</summary>
    public int ReadinessReviewVersion { get; }

    /// <summary><see cref="ProductionReadinessReviewSnapshot.ReviewFingerprint"/> herdado do canário.</summary>
    public Sha256Hash ReadinessReviewFingerprint { get; }

    /// <summary>SHA-1 (40 hex minúsculo) do commit — herdado EXATAMENTE do canário, nunca fornecido pelo chamador (same-build promotion invariant).</summary>
    public string BuildCommitSha { get; }

    /// <summary>Digest do artifact/build — herdado EXATAMENTE do canário (escopo obrigatório item 3: nenhuma promoção de build diferente por equivalência declarada).</summary>
    public Sha256Hash BuildArtifactDigest { get; }

    /// <summary>Fingerprint da policy version — herdado EXATAMENTE do canário.</summary>
    public Sha256Hash PolicyVersionFingerprint { get; }

    /// <summary>Fingerprint da capability matrix — herdado EXATAMENTE do canário.</summary>
    public Sha256Hash CapabilityMatrixFingerprint { get; }

    /// <summary><see cref="CanaryOutcome"/> do canário vinculado, resolvido server-side no instante desta decisão.</summary>
    public CanaryOutcome CanaryOutcomeAtAuthorization { get; }

    /// <summary>
    /// <see cref="ProductionReadinessReviewSnapshot.ReviewVersion"/> VIGENTE no instante desta decisão —
    /// <see langword="null"/> quando nenhum review ainda foi composto para o escopo. Comparado contra
    /// <see cref="ReadinessReviewVersion"/>/<see cref="ReadinessReviewFingerprint"/> para detectar drift
    /// (escopo obrigatório item 3).
    /// </summary>
    public int? CurrentReadinessReviewVersionAtAuthorization { get; }

    /// <summary><see cref="ProductionReadinessReviewSnapshot.ReviewFingerprint"/> VIGENTE no instante desta decisão — <see langword="null"/> quando nenhum review ainda foi composto.</summary>
    public Sha256Hash? CurrentReadinessReviewFingerprintAtAuthorization { get; }

    /// <summary>Desfecho resolvido FRESCO, no instante desta decisão, de CADA controle operacional/M365 (§47.4/§47.5, escopo obrigatório item 4), na ordem determinística do catálogo.</summary>
    public IReadOnlyList<ReadinessControlResult> OperationalControlResults { get; }

    /// <summary>Desfecho agregado — <see cref="GoLiveOutcome.GoLiveAuthorized"/> somente quando <see cref="Blockers"/> está vazio.</summary>
    public GoLiveOutcome Outcome { get; }

    /// <summary>Lista de blockers (escopo obrigatório item 12) — vazia se e somente se <see cref="Outcome"/> é <see cref="GoLiveOutcome.GoLiveAuthorized"/>.</summary>
    public IReadOnlyList<GoLiveBlocker> Blockers { get; }

    /// <summary>
    /// Impressão digital determinística do CONJUNTO DE JULGAMENTO usado para compor esta decisão — chave de
    /// convergência idempotente (replay idêntico nunca duplica versão); qualquer mudança real no canário
    /// vinculado, no review vigente, ou em qualquer controle operacional/M365 produz uma versão nova. NUNCA
    /// cobre identidade/versão/ator/correlação/timestamp (para que uma autorização concorrente idêntica
    /// convirja para a MESMA versão).
    /// </summary>
    public Sha256Hash AuthorizationFingerprint { get; }

    /// <summary>Ator server-side responsável pela decisão (nunca anônimo, nunca alegado pelo payload).</summary>
    public string AuthorizedBy { get; }

    /// <summary>Papel RBAC do ator no instante da decisão.</summary>
    public string AuthorizedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi decidida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset AuthorizedAtUtc { get; }

    /// <summary>Versão do schema desta decisão.</summary>
    public string SchemaVersion { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos — recomputado e validado fail-closed em toda leitura.</summary>
    public Sha256Hash AuthorizationHash { get; }

    /// <summary>
    /// Compõe uma nova decisão de go-live, executando <see cref="GoLiveGateEvaluator"/> internamente sobre as
    /// dependências JÁ RESOLVIDAS pelo chamador (Application layer) — este tipo nunca decide Pass/Fail/Blocked
    /// por si só além de orquestrar a agregação pura, nunca reinterpreta evidência.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="buildCommitSha"/> não é um SHA-1 válido, ou <paramref name="authorizedBy"/>/<paramref name="authorizedByRole"/> vazios.</exception>
    public static GoLiveAuthorizationDecision Compose(
        TenantId tenant,
        ProjectId project,
        GoLiveAuthorizationId authorizationId,
        int authorizationVersion,
        CanaryPlanId canaryPlanId,
        int canaryPlanVersion,
        Sha256Hash canaryPlanFingerprint,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        CanaryOutcome canaryOutcomeAtAuthorization,
        int? currentReadinessReviewVersionAtAuthorization,
        Sha256Hash? currentReadinessReviewFingerprintAtAuthorization,
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> operationalResolvedResults,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc)
    {
        if (authorizationVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationVersion), authorizationVersion, "A versão da decisão deve ser positiva.");
        }

        var normalizedCommitSha = NormalizeCommitSha(buildCommitSha);
        var normalizedAuthorizedBy = TextValue.Require(authorizedBy, nameof(authorizedBy), maxLength: 200);
        var normalizedAuthorizedByRole = TextValue.Require(authorizedByRole, nameof(authorizedByRole), maxLength: 50);
        var canonicalAuthorizedAt = TruncateToMilliseconds(authorizedAtUtc);

        var evaluation = GoLiveGateEvaluator.Evaluate(
            canaryOutcomeAtAuthorization, readinessReviewVersion, readinessReviewFingerprint,
            currentReadinessReviewVersionAtAuthorization, currentReadinessReviewFingerprintAtAuthorization,
            operationalResolvedResults, canonicalAuthorizedAt);

        var authorizationFingerprint = ComputeAuthorizationFingerprint(
            canaryPlanId, canaryPlanVersion, canaryPlanFingerprint, readinessReviewVersion, readinessReviewFingerprint,
            normalizedCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint,
            canaryOutcomeAtAuthorization, currentReadinessReviewVersionAtAuthorization, currentReadinessReviewFingerprintAtAuthorization,
            evaluation.OperationalControlResults);

        var hash = ComputeAuthorizationHash(
            tenant, project, authorizationId, authorizationVersion, evaluation.Outcome, authorizationFingerprint,
            normalizedAuthorizedBy, normalizedAuthorizedByRole, correlation, canonicalAuthorizedAt, CurrentSchemaVersion);

        return new GoLiveAuthorizationDecision(
            tenant, project, authorizationId, authorizationVersion, canaryPlanId, canaryPlanVersion, canaryPlanFingerprint,
            readinessReviewVersion, readinessReviewFingerprint, normalizedCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, canaryOutcomeAtAuthorization, currentReadinessReviewVersionAtAuthorization,
            currentReadinessReviewFingerprintAtAuthorization, evaluation.OperationalControlResults, evaluation.Outcome,
            evaluation.Blockers, authorizationFingerprint, normalizedAuthorizedBy, normalizedAuthorizedByRole, correlation,
            canonicalAuthorizedAt, CurrentSchemaVersion, hash);
    }

    /// <summary>
    /// Reconstrói uma decisão JÁ PERSISTIDA (uso exclusivo da camada de persistência), revalidando
    /// <see cref="AuthorizationFingerprint"/>/<see cref="AuthorizationHash"/> contra os campos REALMENTE
    /// carregados e RE-EXECUTANDO o avaliador puro para confirmar que <paramref name="persistedOutcome"/>
    /// ainda corresponde ao recomputado a partir de <paramref name="operationalControlResults"/>.
    /// </summary>
    /// <exception cref="GoLiveIntegrityViolationException">O fingerprint/hash/outcome persistido diverge do recomputado.</exception>
    public static GoLiveAuthorizationDecision Rehydrate(
        TenantId tenant,
        ProjectId project,
        GoLiveAuthorizationId authorizationId,
        int authorizationVersion,
        CanaryPlanId canaryPlanId,
        int canaryPlanVersion,
        Sha256Hash canaryPlanFingerprint,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        CanaryOutcome canaryOutcomeAtAuthorization,
        int? currentReadinessReviewVersionAtAuthorization,
        Sha256Hash? currentReadinessReviewFingerprintAtAuthorization,
        IReadOnlyList<ReadinessControlResult> operationalControlResults,
        GoLiveOutcome persistedOutcome,
        Sha256Hash persistedAuthorizationFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc,
        string schemaVersion,
        Sha256Hash persistedAuthorizationHash)
    {
        var recomputedFingerprint = ComputeAuthorizationFingerprint(
            canaryPlanId, canaryPlanVersion, canaryPlanFingerprint, readinessReviewVersion, readinessReviewFingerprint,
            buildCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint,
            canaryOutcomeAtAuthorization, currentReadinessReviewVersionAtAuthorization, currentReadinessReviewFingerprintAtAuthorization,
            operationalControlResults);
        if (!string.Equals(recomputedFingerprint.Value, persistedAuthorizationFingerprint.Value, StringComparison.Ordinal))
        {
            throw new GoLiveIntegrityViolationException(
                $"O authorization_fingerprint persistido para a versão {authorizationVersion.ToString(CultureInfo.InvariantCulture)} " +
                "da decisão de go-live não corresponde ao recomputado a partir dos campos carregados — possivelmente adulterado ou corrompido.");
        }

        var operationalResultsById = operationalControlResults.ToDictionary(result => result.ControlId);
        var recomputedEvaluation = GoLiveGateEvaluator.Evaluate(
            canaryOutcomeAtAuthorization, readinessReviewVersion, readinessReviewFingerprint,
            currentReadinessReviewVersionAtAuthorization, currentReadinessReviewFingerprintAtAuthorization,
            operationalResultsById, authorizedAtUtc);
        if (recomputedEvaluation.Outcome != persistedOutcome)
        {
            throw new GoLiveIntegrityViolationException(
                $"O outcome persistido para a versão {authorizationVersion.ToString(CultureInfo.InvariantCulture)} da decisão " +
                "de go-live não corresponde ao recomputado a partir das linhas de controle carregadas — possivelmente adulterado.");
        }

        var recomputedHash = ComputeAuthorizationHash(
            tenant, project, authorizationId, authorizationVersion, persistedOutcome, persistedAuthorizationFingerprint,
            authorizedBy, authorizedByRole, correlation, authorizedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedAuthorizationHash.Value, StringComparison.Ordinal))
        {
            throw new GoLiveIntegrityViolationException(
                $"O authorization_hash persistido para a versão {authorizationVersion.ToString(CultureInfo.InvariantCulture)} da " +
                "decisão de go-live não corresponde ao hash recomputado — possivelmente adulterado ou corrompido.");
        }

        return new GoLiveAuthorizationDecision(
            tenant, project, authorizationId, authorizationVersion, canaryPlanId, canaryPlanVersion, canaryPlanFingerprint,
            readinessReviewVersion, readinessReviewFingerprint, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, canaryOutcomeAtAuthorization, currentReadinessReviewVersionAtAuthorization,
            currentReadinessReviewFingerprintAtAuthorization, operationalControlResults, persistedOutcome,
            recomputedEvaluation.Blockers, persistedAuthorizationFingerprint, authorizedBy, authorizedByRole, correlation,
            authorizedAtUtc, schemaVersion, persistedAuthorizationHash);
    }

    /// <summary>
    /// Impressão digital determinística do conjunto de julgamento, exposta para que a camada de persistência
    /// resolva convergência idempotente ANTES de conhecer a versão a alocar (mesmo padrão de
    /// <see cref="CanaryPlan.ComputePlanFingerprint"/>).
    /// </summary>
    public static Sha256Hash ComputeAuthorizationFingerprint(
        CanaryPlanId canaryPlanId,
        int canaryPlanVersion,
        Sha256Hash canaryPlanFingerprint,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        CanaryOutcome canaryOutcomeAtAuthorization,
        int? currentReadinessReviewVersionAtAuthorization,
        Sha256Hash? currentReadinessReviewFingerprintAtAuthorization,
        IReadOnlyList<ReadinessControlResult> operationalControlResults)
    {
        var parts = new List<string>
        {
            "archivebridge.golive.authorization-fingerprint.v1",
            ReadinessControlCatalog.CurrentCatalogVersion,
            canaryPlanId.Value.ToString("N"),
            canaryPlanVersion.ToString(CultureInfo.InvariantCulture),
            canaryPlanFingerprint.Value,
            readinessReviewVersion.ToString(CultureInfo.InvariantCulture),
            readinessReviewFingerprint.Value,
            buildCommitSha,
            buildArtifactDigest.Value,
            policyVersionFingerprint.Value,
            capabilityMatrixFingerprint.Value,
            ((int)canaryOutcomeAtAuthorization).ToString(CultureInfo.InvariantCulture),
            currentReadinessReviewVersionAtAuthorization?.ToString(CultureInfo.InvariantCulture) ?? "none",
            currentReadinessReviewFingerprintAtAuthorization?.Value ?? "none",
        };

        // Ordem FIXA do subconjunto de catálogo (nunca a ordem de entrada do chamador) — mesmo princípio de
        // ProductionReadinessReviewSnapshot.ComputeReviewFingerprint.
        foreach (var result in operationalControlResults.OrderBy(result => result.ControlId.Value, StringComparer.Ordinal))
        {
            parts.Add(result.ControlId.Value);
            parts.Add(((int)result.Status).ToString(CultureInfo.InvariantCulture));
            parts.Add(((int)result.Evidence.Kind).ToString(CultureInfo.InvariantCulture));
            parts.Add(result.Evidence.Fingerprint.Value);
            parts.Add(result.Evidence.Locator);
            parts.Add(result.ReasonCode);
        }

        return DeterministicHash.Compute(parts);
    }

    private static Sha256Hash ComputeAuthorizationHash(
        TenantId tenant,
        ProjectId project,
        GoLiveAuthorizationId authorizationId,
        int authorizationVersion,
        GoLiveOutcome outcome,
        Sha256Hash authorizationFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset authorizedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(GoLiveAuthorizationDecision),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            authorizationId.Value.ToString("N"),
            authorizationVersion.ToString(CultureInfo.InvariantCulture),
            ((int)outcome).ToString(CultureInfo.InvariantCulture),
            authorizationFingerprint.Value,
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
                "buildCommitSha precisa ser um SHA-1 de commit válido (40 caracteres hexadecimais).", nameof(buildCommitSha));
        }

        return trimmed;
    }

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
