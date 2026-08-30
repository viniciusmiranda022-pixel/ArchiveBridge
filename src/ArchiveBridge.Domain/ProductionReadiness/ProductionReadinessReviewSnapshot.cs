using System.Globalization;
using System.Text.RegularExpressions;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Snapshot IMUTÁVEL e append-only de UMA versão do Production Readiness Review (AB-I8-001, escopo
/// obrigatório item 8) — materializa, de forma determinística, verificável offline e tamper-evident, o
/// desfecho agregado (<see cref="ProductionReadinessOutcome"/>) computado PURAMENTE por
/// <see cref="ProductionReadinessGateEvaluator"/> a partir de evidência canônica já resolvida pelo chamador.
/// NUNCA marca projeto/wave <c>COMPLETED</c>, NUNCA é aprovação de canário/go-live (STOP-THE-LINE), NUNCA
/// escreve em Purview/EXO/Graph/EV/AzCopy/host real.
/// <para>
/// Versionamento monotônico por (tenant, project): a MESMA "impressão digital de revisão"
/// (<see cref="ReviewFingerprint"/>) converge para a MESMA <see cref="ReviewVersion"/> (replay idempotente);
/// uma mudança REAL em qualquer evidência subjacente, no build/commit revisado, na policy version ou na
/// capability matrix produz uma versão nova — nunca sobrescreve uma anterior (AB-I8-001 escopo item 7:
/// detecção de supersession/drift).
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="ReviewFingerprint"/>
/// e <see cref="SnapshotHash"/> a partir dos campos REALMENTE carregados e RE-EXECUTA o avaliador puro sobre
/// os <see cref="ControlResults"/> carregados, recusando fail-closed qualquer divergência entre o
/// <see cref="Outcome"/>/<see cref="Blockers"/> persistidos e os recomputados — mesmo uma adulteração
/// isolada da coluna de outcome (sem tocar as linhas de controle) é detectada.
/// </para>
/// </summary>
public sealed partial record ProductionReadinessReviewSnapshot
{
    /// <summary>Prefixo versionado do schema/catálogo deste snapshot — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.production-readiness.review-snapshot.v1";

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitShaPattern();

    private ProductionReadinessReviewSnapshot(
        TenantId tenant,
        ProjectId project,
        int reviewVersion,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        IReadOnlyList<ReadinessControlResult> controlResults,
        ProductionReadinessOutcome outcome,
        IReadOnlyList<ProductionReadinessBlocker> blockers,
        Sha256Hash reviewFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion,
        Sha256Hash snapshotHash)
    {
        Tenant = tenant;
        Project = project;
        ReviewVersion = reviewVersion;
        BuildCommitSha = buildCommitSha;
        BuildArtifactDigest = buildArtifactDigest;
        PolicyVersionFingerprint = policyVersionFingerprint;
        CapabilityMatrixFingerprint = capabilityMatrixFingerprint;
        ControlResults = controlResults;
        Outcome = outcome;
        Blockers = blockers;
        ReviewFingerprint = reviewFingerprint;
        SubmittedBy = submittedBy;
        SubmittedByRole = submittedByRole;
        Correlation = correlation;
        GeneratedAtUtc = generatedAtUtc;
        SchemaVersion = schemaVersion;
        SnapshotHash = snapshotHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Versão monotônica (1..N) deste snapshot dentro de (tenant, project).</summary>
    public int ReviewVersion { get; }

    /// <summary>SHA-1 (40 hex minúsculo) do commit revisado — nunca uma referência flutuante (branch/tag mutável).</summary>
    public string BuildCommitSha { get; }

    /// <summary>Digest do artifact/build revisado — usado para detectar drift entre o build certificado por <see cref="ArchiveBridge.Domain.Security.BuildProvenanceRecord"/> e o build efetivamente revisado aqui.</summary>
    public Sha256Hash BuildArtifactDigest { get; }

    /// <summary>Fingerprint opaco da policy version efetiva do projeto no instante da revisão.</summary>
    public Sha256Hash PolicyVersionFingerprint { get; }

    /// <summary>Fingerprint opaco da capability matrix efetiva no instante da revisão.</summary>
    public Sha256Hash CapabilityMatrixFingerprint { get; }

    /// <summary>Desfecho resolvido de CADA controle do catálogo, na ordem determinística do catálogo.</summary>
    public IReadOnlyList<ReadinessControlResult> ControlResults { get; }

    /// <summary>Desfecho agregado — <see cref="ProductionReadinessOutcome.ReadyForCanary"/> somente quando <see cref="Blockers"/> está vazio.</summary>
    public ProductionReadinessOutcome Outcome { get; }

    /// <summary>Lista de blockers (AB-I8-001 escopo item 8) — vazia se e somente se <see cref="Outcome"/> é <see cref="ProductionReadinessOutcome.ReadyForCanary"/>.</summary>
    public IReadOnlyList<ProductionReadinessBlocker> Blockers { get; }

    /// <summary>
    /// Impressão digital determinística do CONJUNTO DE EVIDÊNCIA usado para compor este snapshot — chave de
    /// convergência idempotente (replay idêntico nunca duplica versão); qualquer mudança real na evidência,
    /// no build/commit, na policy version ou na capability matrix produz uma versão nova.
    /// </summary>
    public Sha256Hash ReviewFingerprint { get; }

    /// <summary>Ator server-side responsável pela composição (nunca anônimo, nunca alegado pelo payload).</summary>
    public string SubmittedBy { get; }

    /// <summary>Papel RBAC do ator no instante da composição.</summary>
    public string SubmittedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Versão do schema/catálogo deste snapshot.</summary>
    public string SchemaVersion { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos — recomputado e validado fail-closed em toda leitura.</summary>
    public Sha256Hash SnapshotHash { get; }

    /// <summary>
    /// Compõe um novo snapshot a partir de evidência JÁ RESOLVIDA pelo chamador — executa o avaliador puro
    /// (<see cref="ProductionReadinessGateEvaluator"/>) internamente, computando <see cref="ReviewFingerprint"/>
    /// e <see cref="SnapshotHash"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="buildCommitSha"/> não é um SHA-1 válido, ou <paramref name="submittedBy"/>/<paramref name="submittedByRole"/> vazios.</exception>
    public static ProductionReadinessReviewSnapshot Compose(
        TenantId tenant,
        ProjectId project,
        int reviewVersion,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> resolvedControlResults,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc)
    {
        if (reviewVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reviewVersion), reviewVersion, "A versão do snapshot deve ser positiva.");
        }

        var normalizedCommitSha = NormalizeCommitSha(buildCommitSha);
        var normalizedSubmittedBy = TextValue.Require(submittedBy, nameof(submittedBy), maxLength: 200);
        var normalizedSubmittedByRole = TextValue.Require(submittedByRole, nameof(submittedByRole), maxLength: 50);
        var canonicalGeneratedAt = TruncateToMilliseconds(generatedAtUtc);

        var evaluation = ProductionReadinessGateEvaluator.Evaluate(resolvedControlResults, canonicalGeneratedAt);

        // Defesa em profundidade (mesmo padrão "impossível por construção" de PenTestReadinessStatus/
        // RecoveryReadinessRecord.HaFailover): ReadyForCanary só pode chegar aqui quando o avaliador puro já
        // não reportou NENHUM blocker — nunca um caminho alternativo que pudesse produzir divergência.
        if (evaluation.Outcome == ProductionReadinessOutcome.ReadyForCanary && evaluation.Blockers.Count != 0)
        {
            throw new InvalidOperationException(
                "Invariante violada: ProductionReadinessGateEvaluator reportou ReadyForCanary com blockers não-vazios.");
        }

        var reviewFingerprint = ComputeReviewFingerprint(
            normalizedCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, evaluation.ControlResults);
        var hash = ComputeSnapshotHash(
            tenant, project, reviewVersion, normalizedCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, evaluation.Outcome, reviewFingerprint, normalizedSubmittedBy, normalizedSubmittedByRole,
            correlation, canonicalGeneratedAt, CurrentSchemaVersion);

        return new ProductionReadinessReviewSnapshot(
            tenant, project, reviewVersion, normalizedCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, evaluation.ControlResults, evaluation.Outcome, evaluation.Blockers, reviewFingerprint,
            normalizedSubmittedBy, normalizedSubmittedByRole, correlation, canonicalGeneratedAt, CurrentSchemaVersion, hash);
    }

    /// <summary>
    /// Reconstrói um snapshot JÁ PERSISTIDO (uso exclusivo da camada de persistência), revalidando
    /// <see cref="ReviewFingerprint"/> e <see cref="SnapshotHash"/> contra os campos REALMENTE carregados, e
    /// RE-EXECUTANDO o avaliador puro sobre <paramref name="controlResults"/> para confirmar que
    /// <paramref name="persistedOutcome"/>/<paramref name="persistedBlockers"/> não divergem — uma
    /// adulteração isolada da coluna de outcome é detectada mesmo sem tocar as linhas de controle.
    /// </summary>
    /// <exception cref="ProductionReadinessIntegrityViolationException">
    /// O fingerprint/hash persistido diverge do recomputado, ou o outcome/blockers persistidos divergem do
    /// recomputado a partir de <paramref name="controlResults"/>.
    /// </exception>
    public static ProductionReadinessReviewSnapshot Rehydrate(
        TenantId tenant,
        ProjectId project,
        int reviewVersion,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        IReadOnlyList<ReadinessControlResult> controlResults,
        ProductionReadinessOutcome persistedOutcome,
        IReadOnlyList<ProductionReadinessBlocker> persistedBlockers,
        Sha256Hash persistedReviewFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion,
        Sha256Hash persistedSnapshotHash)
    {
        var recomputedReviewFingerprint = ComputeReviewFingerprint(
            buildCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, controlResults);
        if (!string.Equals(recomputedReviewFingerprint.Value, persistedReviewFingerprint.Value, StringComparison.Ordinal))
        {
            throw new ProductionReadinessIntegrityViolationException(
                $"O review_fingerprint persistido para a versão {reviewVersion.ToString(CultureInfo.InvariantCulture)} do " +
                "Production Readiness Review não corresponde ao fingerprint recomputado a partir da evidência carregada " +
                "— snapshot possivelmente adulterado ou corrompido.");
        }

        var resolvedFromLoaded = controlResults.ToDictionary(result => result.ControlId);
        var recomputedEvaluation = ProductionReadinessGateEvaluator.Evaluate(resolvedFromLoaded, generatedAtUtc);
        if (recomputedEvaluation.Outcome != persistedOutcome || recomputedEvaluation.Blockers.Count != persistedBlockers.Count)
        {
            throw new ProductionReadinessIntegrityViolationException(
                $"O outcome/blockers persistidos para a versão {reviewVersion.ToString(CultureInfo.InvariantCulture)} do " +
                "Production Readiness Review não correspondem ao recomputado a partir das linhas de controle carregadas " +
                "— snapshot possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeSnapshotHash(
            tenant, project, reviewVersion, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, persistedOutcome, persistedReviewFingerprint, submittedBy, submittedByRole,
            correlation, generatedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedSnapshotHash.Value, StringComparison.Ordinal))
        {
            throw new ProductionReadinessIntegrityViolationException(
                $"O snapshot_hash persistido para a versão {reviewVersion.ToString(CultureInfo.InvariantCulture)} do " +
                "Production Readiness Review não corresponde ao hash recomputado — snapshot possivelmente adulterado ou corrompido.");
        }

        return new ProductionReadinessReviewSnapshot(
            tenant, project, reviewVersion, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, controlResults, persistedOutcome, persistedBlockers, persistedReviewFingerprint,
            submittedBy, submittedByRole, correlation, generatedAtUtc, schemaVersion, persistedSnapshotHash);
    }

    /// <summary>
    /// Impressão digital determinística do conjunto de evidência, exposta para que a camada de persistência
    /// resolva convergência idempotente ANTES de conhecer a versão a alocar (mesmo padrão de
    /// <see cref="ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation.ReconciliationCertificate.ComputeEvaluationFingerprint"/>).
    /// NUNCA cobre versão/timestamp/ator (para que uma composição concorrente idêntica convirja para a MESMA versão).
    /// </summary>
    /// <remarks>
    /// <paramref name="controlResults"/> é sempre reordenado aqui pela ordem FIXA do catálogo
    /// (<see cref="ReadinessControlCatalog.AllControls"/>) antes de entrar no fingerprint — nunca confiamos na
    /// ordem em que o chamador entrega a lista. Em <see cref="Compose"/> ela já chega nessa ordem (garantida
    /// por <see cref="ProductionReadinessGateEvaluator.Evaluate"/>), mas em <see cref="Rehydrate"/> ela vem de
    /// uma query SQL (<c>ORDER BY control_id</c>, alfabética — nunca a mesma ordem do catálogo, que é agrupada
    /// por gate group). Sem esta reordenação explícita, o mesmo conjunto de evidência produziria fingerprints
    /// DIFERENTES em compose-time vs. rehydrate-time, disparando um falso-positivo de adulteração em toda
    /// leitura.
    /// </remarks>
    public static Sha256Hash ComputeReviewFingerprint(
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        IReadOnlyList<ReadinessControlResult> controlResults)
    {
        var parts = new List<string>
        {
            "archivebridge.production-readiness.review-fingerprint.v1",
            ReadinessControlCatalog.CurrentCatalogVersion,
            buildCommitSha,
            buildArtifactDigest.Value,
            policyVersionFingerprint.Value,
            capabilityMatrixFingerprint.Value,
        };

        // Nunca confiamos na ordem de entrada: reordenamos SEMPRE pela ordem fixa e declarada do catálogo,
        // para que a MESMA evidência sempre produza o MESMO fingerprint byte-a-byte, não importa se
        // controlResults veio do avaliador (ordem do catálogo) ou de uma leitura SQL (ordem alfabética).
        var catalogOrder = CatalogOrderIndex;
        var orderedResults = controlResults.OrderBy(result => catalogOrder[result.ControlId]);
        foreach (var result in orderedResults)
        {
            parts.Add(result.ControlId.Value);
            parts.Add(((int)result.Status).ToString(CultureInfo.InvariantCulture));
            parts.Add(((int)result.Evidence.Kind).ToString(CultureInfo.InvariantCulture));
            parts.Add(result.Evidence.Fingerprint.Value);
            parts.Add(result.ReasonCode);
        }

        return DeterministicHash.Compute(parts);
    }

    private static readonly IReadOnlyDictionary<ReadinessControlId, int> CatalogOrderIndex =
        ReadinessControlCatalog.AllControls
            .Select((definition, index) => (definition.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);

    private static Sha256Hash ComputeSnapshotHash(
        TenantId tenant,
        ProjectId project,
        int reviewVersion,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        ProductionReadinessOutcome outcome,
        Sha256Hash reviewFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(ProductionReadinessReviewSnapshot),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            reviewVersion.ToString(CultureInfo.InvariantCulture),
            buildCommitSha,
            buildArtifactDigest.Value,
            policyVersionFingerprint.Value,
            capabilityMatrixFingerprint.Value,
            ((int)outcome).ToString(CultureInfo.InvariantCulture),
            reviewFingerprint.Value,
            submittedBy,
            submittedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(generatedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
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
