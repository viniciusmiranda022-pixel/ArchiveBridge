using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Recovery;

/// <summary>
/// Evidência IMUTÁVEL e append-only de UM exercício de recovery readiness (AB-I7-005) — restore drill,
/// pending-work rebuild, artifact/evidence recovery ou avaliação de HA/failover — tenant/project-scoped,
/// tamper-evident e determinístico.
/// <para>
/// Versionamento monotônico por (tenant, project, tipo de exercício): a MESMA "impressão digital do
/// exercício" (<see cref="ExerciseFingerprint"/>) converge para a MESMA <see cref="ExerciseVersion"/>
/// (replay idempotente); um resultado REALMENTE diferente (nova medição, novo desfecho, nova evidência)
/// produz uma versão nova — nunca sobrescreve uma anterior.
/// </para>
/// <para>
/// A ÚNICA forma de obter <see cref="RecoveryReadinessStatus.Pass"/> é <see cref="Pass"/>, que exige uma
/// <see cref="RecoveryObjectiveMeasurement"/> REAL e, quando há um alvo objetivo documentado
/// (<see cref="RecoveryObjective"/> != <see cref="RecoveryObjective.None"/>), que a duração medida não o
/// exceda — nunca é possível declarar sucesso por configuração/alegação sem execução (invariante do work
/// order: "Unknown/NotMeasured nunca vira Ready/Pass").
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="RecordHash"/> a
/// partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência.
/// </para>
/// </summary>
public sealed record RecoveryReadinessRecord
{
    /// <summary>Prefixo versionado do schema deste registro — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.recovery.readiness-record.v1";

    /// <summary>Tamanho máximo de <see cref="FailureDomain"/>/<see cref="Notes"/> — nunca segredo/PII, só metadados técnicos (STOP-THE-LINE do work order).</summary>
    private const int MaxTextLength = 1000;

    /// <summary>
    /// Fingerprint canônico usado quando nenhuma evidência foi produzida ainda (<see cref="NotMeasured"/>) —
    /// SEMPRE um <see cref="Sha256Hash"/> concreto (nunca <see langword="default"/>/nulo), para que o campo
    /// persistido <c>evidence_fingerprint</c> nunca precise de NULL e a revalidação de
    /// <see cref="ExerciseFingerprint"/> em <see cref="Rehydrate"/> nunca dependa de tratar nulo como igual a
    /// vazio.
    /// </summary>
    public static readonly Sha256Hash NoEvidenceFingerprint =
        DeterministicHash.Compute(["archivebridge.recovery.readiness-record.no-evidence.v1"]);

    private RecoveryReadinessRecord(
        TenantId tenant,
        ProjectId project,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc,
        string schemaVersion,
        Sha256Hash exerciseFingerprint,
        Sha256Hash recordHash)
    {
        Tenant = tenant;
        Project = project;
        ExerciseType = exerciseType;
        ExerciseVersion = exerciseVersion;
        Status = status;
        Objective = objective;
        ObjectiveThreshold = objectiveThreshold;
        Measurement = measurement;
        EvidenceFingerprint = evidenceFingerprint;
        FailureDomain = failureDomain;
        Notes = notes;
        ExecutedBy = executedBy;
        ExecutedByRole = executedByRole;
        Correlation = correlation;
        ExecutedAtUtc = executedAtUtc;
        SchemaVersion = schemaVersion;
        ExerciseFingerprint = exerciseFingerprint;
        RecordHash = recordHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Tipo de exercício de recovery readiness.</summary>
    public RecoveryExerciseType ExerciseType { get; }

    /// <summary>Versão monotônica (1..N) deste registro dentro de (tenant, project, tipo de exercício).</summary>
    public int ExerciseVersion { get; }

    /// <summary>Desfecho canônico do exercício — nunca <see cref="RecoveryReadinessStatus.Pass"/> sem medição real e objetivo atingido.</summary>
    public RecoveryReadinessStatus Status { get; }

    /// <summary>Objetivo de recuperação medido, se houver (<see cref="RecoveryObjective.None"/> quando não aplicável).</summary>
    public RecoveryObjective Objective { get; }

    /// <summary>Alvo documentado do objetivo (ex.: RTO &lt;= 4h) — <see langword="null"/> quando <see cref="Objective"/> é <see cref="RecoveryObjective.None"/>.</summary>
    public TimeSpan? ObjectiveThreshold { get; }

    /// <summary>Medição REAL do exercício — presente se e somente se o exercício foi de fato executado.</summary>
    public RecoveryObjectiveMeasurement? Measurement { get; }

    /// <summary>
    /// Hash determinístico do conjunto de evidência canônica verificado por este exercício (ex.: hash
    /// agregado das entidades reidratadas/validadas) — nunca segredo/PII, apenas o digest.
    /// </summary>
    public Sha256Hash EvidenceFingerprint { get; }

    /// <summary>
    /// Failure domain documentado (AB-I7-005 item 10) quando <see cref="Status"/> é
    /// <see cref="RecoveryReadinessStatus.Blocked"/> por limitação arquitetural (ex.: proteção de segredo
    /// single-node sem failover comprovado) — vazio quando não aplicável.
    /// </summary>
    public string FailureDomain { get; }

    /// <summary>Notas técnicas objetivas do exercício — nunca segredo/PII/caminho físico/conteúdo de mailbox.</summary>
    public string Notes { get; }

    /// <summary>Ator server-side responsável pela execução do exercício.</summary>
    public string ExecutedBy { get; }

    /// <summary>Papel RBAC do ator no instante da execução.</summary>
    public string ExecutedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset ExecutedAtUtc { get; }

    /// <summary>Versão do schema deste registro.</summary>
    public string SchemaVersion { get; }

    /// <summary>
    /// Impressão digital determinística do RESULTADO do exercício (item de convergência idempotente) — a
    /// MESMA combinação de desfecho/objetivo/medição/evidência/failure-domain/notas produz a MESMA versão,
    /// independentemente de quantas vezes o exercício for reexecutado com o mesmo resultado; um resultado
    /// REALMENTE diferente produz uma versão nova. Nunca cobre versão/timestamp/ator (para que reexecuções
    /// concorrentes idênticas convirjam para a MESMA versão).
    /// </summary>
    public Sha256Hash ExerciseFingerprint { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos — recomputado e validado fail-closed em toda leitura.</summary>
    public Sha256Hash RecordHash { get; }

    /// <summary>
    /// Registra um exercício que foi REALMENTE executado e cujo objetivo (quando houver) foi atingido pela
    /// medição observada.
    /// </summary>
    /// <exception cref="RecoveryReadinessObjectiveNotMetException">
    /// <paramref name="objective"/> exige alvo (<paramref name="objectiveThreshold"/> presente) e a duração
    /// medida o excede.
    /// </exception>
    public static RecoveryReadinessRecord Pass(
        TenantId tenant,
        ProjectId project,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement measurement,
        Sha256Hash evidenceFingerprint,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc)
    {
        if (exerciseType == RecoveryExerciseType.HaFailover)
        {
            // STOP-THE-LINE do work order (AB-I7-005): "alegar HA para componentes single-node sem
            // failover comprovado" é fora de escopo deste Passo — nenhum mecanismo de failover
            // aprovado existe hoje na baseline aceita, então NENHUM código pode construir um Pass
            // para este tipo de exercício (bloqueio estrutural, não apenas convenção de chamada).
            throw new RecoveryReadinessObjectiveNotMetException(
                "HaFailover não pode resultar em Pass nesta baseline — nenhum mecanismo de failover aprovado " +
                "existe hoje; o desfecho permanece explicitamente Blocked (item 9/10 e STOP-THE-LINE do work order).");
        }

        if (objectiveThreshold is { } threshold && measurement.Elapsed > threshold)
        {
            throw new RecoveryReadinessObjectiveNotMetException(
                $"A duração medida ({measurement.Elapsed}) excede o alvo objetivo documentado ({threshold}) — " +
                "não é possível declarar Pass quando o objetivo não foi atingido pela medição real.");
        }

        return Create(
            tenant, project, exerciseType, exerciseVersion, RecoveryReadinessStatus.Pass, objective, objectiveThreshold,
            measurement, evidenceFingerprint, failureDomain: string.Empty, notes, executedBy, executedByRole,
            correlation, executedAtUtc);
    }

    /// <summary>
    /// Registra um exercício explicitamente bloqueado — por limitação arquitetural comprovada (HA
    /// não-comprovada, <paramref name="measurement"/> ausente) ou por falha/objetivo não atingido durante um
    /// drill real (<paramref name="measurement"/> presente).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="failureDomain"/> vazio.</exception>
    public static RecoveryReadinessRecord Blocked(
        TenantId tenant,
        ProjectId project,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc) =>
        Create(
            tenant, project, exerciseType, exerciseVersion, RecoveryReadinessStatus.Blocked, objective, objectiveThreshold,
            measurement, evidenceFingerprint, failureDomain, notes, executedBy, executedByRole, correlation, executedAtUtc);

    /// <summary>Registra que a capacidade ainda não foi exercitada por nenhum drill/teste aplicável — fail-closed default, nunca <see cref="RecoveryReadinessStatus.Pass"/>.</summary>
    public static RecoveryReadinessRecord NotMeasured(
        TenantId tenant,
        ProjectId project,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc) =>
        Create(
            tenant, project, exerciseType, exerciseVersion, RecoveryReadinessStatus.NotMeasured, objective, objectiveThreshold,
            measurement: null, evidenceFingerprint: NoEvidenceFingerprint, failureDomain: string.Empty, notes, executedBy,
            executedByRole, correlation, executedAtUtc);

    private static RecoveryReadinessRecord Create(
        TenantId tenant,
        ProjectId project,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc)
    {
        if (exerciseVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exerciseVersion), exerciseVersion, "A versão do exercício deve ser positiva.");
        }

        if (status == RecoveryReadinessStatus.Pass && measurement is null)
        {
            throw new RecoveryReadinessObjectiveNotMetException(
                "Pass exige uma medição real do exercício — não é possível declarar sucesso sem execução.");
        }

        if (status == RecoveryReadinessStatus.NotMeasured && measurement is not null)
        {
            throw new ArgumentException("NotMeasured não pode carregar uma medição — o exercício ainda não foi executado.", nameof(measurement));
        }

        if (objective == RecoveryObjective.None && objectiveThreshold is not null)
        {
            throw new ArgumentException("Nenhum alvo objetivo é aplicável quando Objective é None.", nameof(objectiveThreshold));
        }

        if (objective != RecoveryObjective.None && objectiveThreshold is { } threshold && threshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(objectiveThreshold), threshold, "O alvo objetivo deve ser positivo.");
        }

        var normalizedFailureDomain = string.IsNullOrWhiteSpace(failureDomain)
            ? string.Empty
            : TextValue.Require(failureDomain, nameof(failureDomain), MaxTextLength);

        if (status == RecoveryReadinessStatus.Blocked && measurement is null && normalizedFailureDomain.Length == 0)
        {
            throw new ArgumentException(
                "Blocked sem medição (limitação arquitetural) exige failureDomain documentado (item 10 do work order).",
                nameof(failureDomain));
        }

        var normalizedNotes = string.IsNullOrWhiteSpace(notes) ? string.Empty : TextValue.Require(notes, nameof(notes), MaxTextLength);
        var normalizedExecutedBy = TextValue.Require(executedBy, nameof(executedBy), maxLength: 200);
        var normalizedExecutedByRole = TextValue.Require(executedByRole, nameof(executedByRole), maxLength: 50);
        var canonicalExecutedAt = TruncateToMilliseconds(executedAtUtc);

        var exerciseFingerprint = ComputeExerciseFingerprint(
            status, objective, objectiveThreshold, measurement, evidenceFingerprint, normalizedFailureDomain, normalizedNotes);

        var hash = ComputeRecordHash(
            tenant, project, exerciseType, exerciseVersion, status, objective, objectiveThreshold, measurement,
            evidenceFingerprint, normalizedFailureDomain, normalizedNotes, exerciseFingerprint, normalizedExecutedBy,
            normalizedExecutedByRole, correlation, canonicalExecutedAt, CurrentSchemaVersion);

        return new RecoveryReadinessRecord(
            tenant, project, exerciseType, exerciseVersion, status, objective, objectiveThreshold, measurement,
            evidenceFingerprint, normalizedFailureDomain, normalizedNotes, normalizedExecutedBy, normalizedExecutedByRole,
            correlation, canonicalExecutedAt, CurrentSchemaVersion, exerciseFingerprint, hash);
    }

    /// <summary>
    /// Reconstrói um registro JÁ PERSISTIDO (uso exclusivo da camada de persistência), revalidando
    /// <see cref="ExerciseFingerprint"/> e <see cref="RecordHash"/> contra os campos REALMENTE carregados
    /// (fail-closed).
    /// </summary>
    /// <exception cref="RecoveryReadinessIntegrityViolationException">Fingerprint/hash persistidos divergem dos recomputados.</exception>
    public static RecoveryReadinessRecord Rehydrate(
        TenantId tenant,
        ProjectId project,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        Sha256Hash persistedExerciseFingerprint,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc,
        string schemaVersion,
        Sha256Hash persistedRecordHash)
    {
        var recomputedExerciseFingerprint = ComputeExerciseFingerprint(
            status, objective, objectiveThreshold, measurement, evidenceFingerprint, failureDomain, notes);

        if (!string.Equals(recomputedExerciseFingerprint.Value, persistedExerciseFingerprint.Value, StringComparison.Ordinal))
        {
            throw new RecoveryReadinessIntegrityViolationException(
                $"O exercise_fingerprint persistido para a versão {exerciseVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"do registro de recovery readiness ({exerciseType}) não corresponde ao fingerprint recomputado a partir " +
                "dos campos carregados — registro possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeRecordHash(
            tenant, project, exerciseType, exerciseVersion, status, objective, objectiveThreshold, measurement,
            evidenceFingerprint, failureDomain, notes, persistedExerciseFingerprint, executedBy, executedByRole,
            correlation, executedAtUtc, schemaVersion);

        if (!string.Equals(recomputedHash.Value, persistedRecordHash.Value, StringComparison.Ordinal))
        {
            throw new RecoveryReadinessIntegrityViolationException(
                $"O record_hash persistido para a versão {exerciseVersion.ToString(CultureInfo.InvariantCulture)} do " +
                $"registro de recovery readiness ({exerciseType}) não corresponde ao hash recomputado a partir dos " +
                "campos carregados — registro possivelmente adulterado ou corrompido.");
        }

        return new RecoveryReadinessRecord(
            tenant, project, exerciseType, exerciseVersion, status, objective, objectiveThreshold, measurement,
            evidenceFingerprint, failureDomain, notes, executedBy, executedByRole, correlation, executedAtUtc,
            schemaVersion, persistedExerciseFingerprint, persistedRecordHash);
    }

    /// <summary>
    /// Impressão digital determinística do RESULTADO do exercício, exposta para que a camada de persistência
    /// resolva convergência idempotente ANTES de conhecer a versão a alocar (mesmo padrão de
    /// <see cref="TargetIngestion.Purview.Reconciliation.ReconciliationCertificate.ComputeEvaluationFingerprint"/>).
    /// </summary>
    public static Sha256Hash ComputeExerciseFingerprint(
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes) =>
        DeterministicHash.Compute(
        [
            "archivebridge.recovery.readiness-exercise-fingerprint.v1",
            ((int)status).ToString(CultureInfo.InvariantCulture),
            ((int)objective).ToString(CultureInfo.InvariantCulture),
            objectiveThreshold?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "none",
            measurement?.StartedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "none",
            measurement?.CompletedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "none",
            evidenceFingerprint.Value,
            failureDomain,
            notes,
        ]);

    private static Sha256Hash ComputeRecordHash(
        TenantId tenant,
        ProjectId project,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        Sha256Hash exerciseFingerprint,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(RecoveryReadinessRecord),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            ((int)exerciseType).ToString(CultureInfo.InvariantCulture),
            exerciseVersion.ToString(CultureInfo.InvariantCulture),
            exerciseFingerprint.Value,
            executedBy,
            executedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(executedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
