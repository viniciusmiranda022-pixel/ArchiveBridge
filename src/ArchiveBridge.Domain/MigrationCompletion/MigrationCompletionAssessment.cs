using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Avaliação IMUTÁVEL e append-only de UMA versão do gate de encerramento de migração (AB-I8-010, runbook
/// §49, escopo obrigatório item 7) — materializa, de forma determinística, verificável offline e
/// tamper-evident, o desfecho agregado (<see cref="MigrationCompletionOutcome"/>) computado PURAMENTE por
/// <see cref="MigrationCompletionGateEvaluator"/> a partir de evidência canônica já resolvida pelo chamador.
/// NUNCA marca migração/projeto/wave <c>Completed</c> (ver <see cref="MigrationCompletionOutcome"/>), NUNCA
/// executa decommission/exclusão destrutiva/revogação irreversível, NUNCA escreve em Purview/EXO/Graph/EV
/// real (STOP-THE-LINE).
/// <para>
/// <see cref="AnchorWave"/>/<see cref="AnchorPlannedJobName"/> identificam a onda/plano de import job cuja
/// evidência de reconciliação/resultados do provider ancora <see cref="MigrationCompletionCriterionCatalog"/>'s
/// dois critérios <c>SystemDerived</c> — este repositório não expõe hoje uma consulta "todas as ondas de um
/// projeto" (<see cref="ArchiveBridge.Contracts.Waves.IWaveStore"/> é indexado por onda individual), então o
/// escopo de evidência técnica é explicitamente nomeado pelo operador que solicita a avaliação, exatamente
/// como o próprio <see cref="ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation.ReconciliationCertificate"/>
/// já é, por natureza, escopado a (onda, plano).
/// </para>
/// <para>
/// Versionamento monotônico por (tenant, project): a MESMA "impressão digital de avaliação"
/// (<see cref="AssessmentFingerprint"/>) converge para a MESMA <see cref="AssessmentVersion"/> (replay
/// idempotente); uma mudança REAL em qualquer critério resolvido produz uma versão nova — nunca sobrescreve
/// uma anterior.
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="AssessmentFingerprint"/>
/// e <see cref="AssessmentHash"/> a partir dos campos REALMENTE carregados e RE-EXECUTA o avaliador puro sobre
/// os <see cref="CriterionResults"/> carregados, recusando fail-closed qualquer divergência entre o
/// <see cref="Outcome"/> persistido e o recomputado.
/// </para>
/// </summary>
public sealed record MigrationCompletionAssessment
{
    /// <summary>Prefixo versionado do schema/catálogo desta avaliação — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.migration-completion.assessment.v1";

    private MigrationCompletionAssessment(
        TenantId tenant,
        ProjectId project,
        int assessmentVersion,
        WaveId anchorWave,
        PurviewImportJobName anchorPlannedJobName,
        IReadOnlyList<MigrationCompletionCriterionResult> criterionResults,
        MigrationCompletionOutcome outcome,
        IReadOnlyList<MigrationCompletionBlocker> blockers,
        Sha256Hash assessmentFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion,
        Sha256Hash assessmentHash)
    {
        Tenant = tenant;
        Project = project;
        AssessmentVersion = assessmentVersion;
        AnchorWave = anchorWave;
        AnchorPlannedJobName = anchorPlannedJobName;
        CriterionResults = criterionResults;
        Outcome = outcome;
        Blockers = blockers;
        AssessmentFingerprint = assessmentFingerprint;
        SubmittedBy = submittedBy;
        SubmittedByRole = submittedByRole;
        Correlation = correlation;
        GeneratedAtUtc = generatedAtUtc;
        SchemaVersion = schemaVersion;
        AssessmentHash = assessmentHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Versão monotônica (1..N) desta avaliação dentro de (tenant, project).</summary>
    public int AssessmentVersion { get; }

    /// <summary>Onda cuja evidência técnica ancora os critérios <c>SystemDerived</c> desta avaliação.</summary>
    public WaveId AnchorWave { get; }

    /// <summary>Plano de import job cuja evidência técnica ancora os critérios <c>SystemDerived</c> desta avaliação.</summary>
    public PurviewImportJobName AnchorPlannedJobName { get; }

    /// <summary>Desfecho resolvido de CADA critério do catálogo (§49), na ordem determinística do catálogo.</summary>
    public IReadOnlyList<MigrationCompletionCriterionResult> CriterionResults { get; }

    /// <summary>Desfecho agregado — <see cref="MigrationCompletionOutcome.Eligible"/> somente quando <see cref="Blockers"/> está vazio.</summary>
    public MigrationCompletionOutcome Outcome { get; }

    /// <summary>Lista de blockers (escopo obrigatório item 12) — vazia se e somente se <see cref="Outcome"/> é <see cref="MigrationCompletionOutcome.Eligible"/>.</summary>
    public IReadOnlyList<MigrationCompletionBlocker> Blockers { get; }

    /// <summary>
    /// Impressão digital determinística do CONJUNTO DE EVIDÊNCIA usado para compor esta avaliação — chave de
    /// convergência idempotente; qualquer mudança real em qualquer critério resolvido produz uma versão nova.
    /// NUNCA cobre versão/ator/correlação/timestamp.
    /// </summary>
    public Sha256Hash AssessmentFingerprint { get; }

    /// <summary>Ator server-side responsável pela composição (nunca anônimo, nunca alegado pelo payload).</summary>
    public string SubmittedBy { get; }

    /// <summary>Papel RBAC do ator no instante da composição.</summary>
    public string SubmittedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi gerada (append-only — nunca mutado depois).</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Versão do schema desta avaliação.</summary>
    public string SchemaVersion { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos — recomputado e validado fail-closed em toda leitura.</summary>
    public Sha256Hash AssessmentHash { get; }

    /// <summary>Compõe uma nova avaliação, executando <see cref="MigrationCompletionGateEvaluator"/> internamente.</summary>
    /// <exception cref="ArgumentException"><paramref name="submittedBy"/>/<paramref name="submittedByRole"/> vazios.</exception>
    public static MigrationCompletionAssessment Compose(
        TenantId tenant,
        ProjectId project,
        int assessmentVersion,
        WaveId anchorWave,
        PurviewImportJobName anchorPlannedJobName,
        IReadOnlyDictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> resolvedCriterionResults,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc)
    {
        if (assessmentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assessmentVersion), assessmentVersion, "A versão da avaliação deve ser positiva.");
        }

        var normalizedSubmittedBy = TextValue.Require(submittedBy, nameof(submittedBy), maxLength: 200);
        var normalizedSubmittedByRole = TextValue.Require(submittedByRole, nameof(submittedByRole), maxLength: 50);
        var canonicalGeneratedAt = TruncateToMilliseconds(generatedAtUtc);

        var evaluation = MigrationCompletionGateEvaluator.Evaluate(resolvedCriterionResults, canonicalGeneratedAt);

        var assessmentFingerprint = ComputeAssessmentFingerprint(anchorWave, anchorPlannedJobName, evaluation.CriterionResults);
        var hash = ComputeAssessmentHash(
            tenant, project, assessmentVersion, anchorWave, anchorPlannedJobName, evaluation.Outcome, assessmentFingerprint,
            normalizedSubmittedBy, normalizedSubmittedByRole, correlation, canonicalGeneratedAt, CurrentSchemaVersion);

        return new MigrationCompletionAssessment(
            tenant, project, assessmentVersion, anchorWave, anchorPlannedJobName, evaluation.CriterionResults, evaluation.Outcome,
            evaluation.Blockers, assessmentFingerprint, normalizedSubmittedBy, normalizedSubmittedByRole, correlation,
            canonicalGeneratedAt, CurrentSchemaVersion, hash);
    }

    /// <summary>Reconstrói uma avaliação JÁ PERSISTIDA, revalidando fingerprint/hash e re-executando o avaliador puro.</summary>
    /// <exception cref="MigrationCompletionIntegrityViolationException">Fingerprint/hash/outcome persistidos divergem dos recomputados.</exception>
    public static MigrationCompletionAssessment Rehydrate(
        TenantId tenant,
        ProjectId project,
        int assessmentVersion,
        WaveId anchorWave,
        PurviewImportJobName anchorPlannedJobName,
        IReadOnlyList<MigrationCompletionCriterionResult> criterionResults,
        MigrationCompletionOutcome persistedOutcome,
        Sha256Hash persistedAssessmentFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion,
        Sha256Hash persistedAssessmentHash)
    {
        var recomputedFingerprint = ComputeAssessmentFingerprint(anchorWave, anchorPlannedJobName, criterionResults);
        if (!string.Equals(recomputedFingerprint.Value, persistedAssessmentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new MigrationCompletionIntegrityViolationException(
                $"O assessment_fingerprint persistido para a versão {assessmentVersion.ToString(CultureInfo.InvariantCulture)} " +
                "da avaliação de encerramento não corresponde ao recomputado — possivelmente adulterado ou corrompido.");
        }

        var criterionResultsById = criterionResults.ToDictionary(result => result.CriterionId);
        var recomputedEvaluation = MigrationCompletionGateEvaluator.Evaluate(criterionResultsById, generatedAtUtc);
        if (recomputedEvaluation.Outcome != persistedOutcome)
        {
            throw new MigrationCompletionIntegrityViolationException(
                $"O outcome persistido para a versão {assessmentVersion.ToString(CultureInfo.InvariantCulture)} da avaliação " +
                "de encerramento não corresponde ao recomputado a partir das linhas de critério carregadas — possivelmente adulterado.");
        }

        var recomputedHash = ComputeAssessmentHash(
            tenant, project, assessmentVersion, anchorWave, anchorPlannedJobName, persistedOutcome, persistedAssessmentFingerprint,
            submittedBy, submittedByRole, correlation, generatedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedAssessmentHash.Value, StringComparison.Ordinal))
        {
            throw new MigrationCompletionIntegrityViolationException(
                $"O assessment_hash persistido para a versão {assessmentVersion.ToString(CultureInfo.InvariantCulture)} da " +
                "avaliação de encerramento não corresponde ao hash recomputado — possivelmente adulterado ou corrompido.");
        }

        return new MigrationCompletionAssessment(
            tenant, project, assessmentVersion, anchorWave, anchorPlannedJobName, criterionResults, persistedOutcome,
            recomputedEvaluation.Blockers, persistedAssessmentFingerprint, submittedBy, submittedByRole, correlation,
            generatedAtUtc, schemaVersion, persistedAssessmentHash);
    }

    private static Sha256Hash ComputeAssessmentFingerprint(
        WaveId anchorWave, PurviewImportJobName anchorPlannedJobName, IReadOnlyList<MigrationCompletionCriterionResult> criterionResults)
    {
        var parts = new List<string>
        {
            "archivebridge.migration-completion.assessment-fingerprint.v1",
            MigrationCompletionCriterionCatalog.CurrentCatalogVersion,
            anchorWave.Value.ToString("N"),
            anchorPlannedJobName.Value,
        };

        var catalogOrder = CatalogOrderIndex;
        foreach (var result in criterionResults.OrderBy(result => catalogOrder[result.CriterionId]))
        {
            parts.Add(result.CriterionId.Value);
            parts.Add(((int)result.Status).ToString(CultureInfo.InvariantCulture));
            parts.Add(((int)result.Evidence.Kind).ToString(CultureInfo.InvariantCulture));
            parts.Add(result.Evidence.Fingerprint.Value);
            parts.Add(result.ReasonCode);
        }

        return DeterministicHash.Compute(parts);
    }

    private static readonly IReadOnlyDictionary<MigrationCompletionCriterionId, int> CatalogOrderIndex =
        MigrationCompletionCriterionCatalog.AllCriteria
            .Select((definition, index) => (definition.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);

    private static Sha256Hash ComputeAssessmentHash(
        TenantId tenant,
        ProjectId project,
        int assessmentVersion,
        WaveId anchorWave,
        PurviewImportJobName anchorPlannedJobName,
        MigrationCompletionOutcome outcome,
        Sha256Hash assessmentFingerprint,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset generatedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(MigrationCompletionAssessment),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            assessmentVersion.ToString(CultureInfo.InvariantCulture),
            anchorWave.Value.ToString("N"),
            anchorPlannedJobName.Value,
            ((int)outcome).ToString(CultureInfo.InvariantCulture),
            assessmentFingerprint.Value,
            submittedBy,
            submittedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(generatedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
