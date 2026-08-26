using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Decisão IMUTÁVEL e append-only de UMA versão do workflow de disposition humano/auditável (AB-I6-010)
/// sobre UMA exceção técnica de reconciliação já materializada pelo Passo 3 (AB-I6-007) — nunca altera nem
/// mascara o resultado técnico (<see cref="ReconciliationDisposition"/>) da avaliação de origem, apenas
/// adiciona uma camada de decisão auditável por cima dela. Nunca um certificate, <c>ReconciliationOutcome</c>
/// ou conclusão de wave/projeto (STOP-THE-LINE).
/// <para>
/// A identidade completa de UMA exceção é (onda, plano, versão de avaliação, <see cref="ItemKind"/>,
/// <see cref="ItemKey"/>) — cada versão NOVA de avaliação (Passo 3) recomeça o histórico de decisão do zero
/// para o mesmo item lógico: uma decisão sobre a versão N nunca "carrega" automaticamente para a versão N+1
/// (item 8 — qualquer disposition sobre uma avaliação superseded é recusada fail-closed pela Application
/// ANTES de chegar aqui; uma nova decisão explícita é sempre exigida contra a avaliação vigente).
/// </para>
/// <para>
/// Versionamento monotônico por exceção: a MESMA "impressão digital da decisão" (<see cref="DecisionFingerprint"/>,
/// item 9) converge para a MESMA <see cref="DecisionVersion"/> (replay idempotente); uma decisão REALMENTE
/// diferente (status/motivo/comentário/ator) produz uma nova versão explícita — nunca sobrescreve a anterior
/// (item 9-10: sem last-write-wins silencioso).
/// </para>
/// <para>
/// A persistência é fronteira NÃO CONFIÁVEL: <see cref="Rehydrate"/> recomputa <see cref="DecisionHash"/> a
/// partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência (mesmo princípio de
/// <see cref="ReconciliationAssessment.Rehydrate"/>).
/// </para>
/// </summary>
public sealed record ReconciliationExceptionDecision
{
    private ReconciliationExceptionDecision(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        int decisionVersion,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion,
        string? comment,
        string decidedBy,
        string decidedByRole,
        CorrelationId correlation,
        DateTimeOffset decidedAtUtc,
        Sha256Hash decisionFingerprint,
        Sha256Hash decisionHash)
    {
        Tenant = tenant;
        Project = project;
        Wave = wave;
        PlannedJobName = plannedJobName;
        AssessmentVersion = assessmentVersion;
        AssessmentSourceFingerprint = assessmentSourceFingerprint;
        ItemKind = itemKind;
        ItemKey = itemKey;
        TechnicalDisposition = technicalDisposition;
        DecisionVersion = decisionVersion;
        Status = status;
        ReasonCode = reasonCode;
        ReasonCodeCatalogVersion = reasonCodeCatalogVersion;
        Comment = comment;
        DecidedBy = decidedBy;
        DecidedByRole = decidedByRole;
        Correlation = correlation;
        DecidedAtUtc = decidedAtUtc;
        DecisionFingerprint = decisionFingerprint;
        DecisionHash = decisionHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Plano de import job cuja avaliação de reconciliação contém o item.</summary>
    public PurviewImportJobName PlannedJobName { get; }

    /// <summary>Versão da avaliação de reconciliação (Passo 3) vigente no instante da decisão.</summary>
    public int AssessmentVersion { get; }

    /// <summary>
    /// <see cref="ReconciliationAssessment.SourceFingerprint"/> da avaliação no instante da decisão — capturado
    /// para reforçar (defesa em profundidade) a ligação tamper-evident entre a decisão e a evidência técnica
    /// exata sobre a qual ela foi tomada.
    /// </summary>
    public Sha256Hash AssessmentSourceFingerprint { get; }

    /// <summary>Lista filha (PST ou archive) a que pertence o item.</summary>
    public ReconciliationExceptionItemKind ItemKind { get; }

    /// <summary>
    /// Identificador opaco do item dentro da lista (<see cref="PstReconciliationItem.RemoteName"/> ou
    /// <see cref="ArchiveReconciliationItem.Archive"/>) — o caller nunca fornece mais do que isto (item 2).
    /// </summary>
    public string ItemKey { get; }

    /// <summary>
    /// Resultado técnico (<see cref="ReconciliationDisposition"/>) do item, capturado no instante da decisão —
    /// nunca alterado por ela; permite auditar exatamente sobre qual fato técnico a decisão foi tomada.
    /// </summary>
    public ReconciliationDisposition TechnicalDisposition { get; }

    /// <summary>Versão monotônica (1..N) desta decisão dentro da exceção (onda, plano, versão de avaliação, item).</summary>
    public int DecisionVersion { get; }

    /// <summary>Estado explícito decidido.</summary>
    public ReconciliationExceptionDecisionStatus Status { get; }

    /// <summary>Motivo controlado do catálogo fechado (item 15).</summary>
    public ReconciliationExceptionReasonCode ReasonCode { get; }

    /// <summary>Versão do catálogo de motivos vigente no instante da decisão.</summary>
    public byte ReasonCodeCatalogVersion { get; }

    /// <summary>Comentário livre opcional, já sanitizado/limitado (item 16) — nunca a única autoridade semântica da decisão.</summary>
    public string? Comment { get; }

    /// <summary>Ator server-side responsável pela decisão (nunca anônimo).</summary>
    public string DecidedBy { get; }

    /// <summary>Papel RBAC do ator no instante da decisão (item 5 — auditável independentemente do papel atual do ator).</summary>
    public string DecidedByRole { get; }

    /// <summary>Correlação com a requisição/trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset DecidedAtUtc { get; }

    /// <summary>
    /// Impressão digital determinística do CONTEÚDO da decisão (item 9) — chave de convergência idempotente:
    /// a MESMA decisão (mesmo status/motivo/comentário/ator sobre a MESMA exceção/versão de avaliação)
    /// produz a MESMA impressão digital, independentemente de quando/quantas vezes for reenviada.
    /// </summary>
    public Sha256Hash DecisionFingerprint { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash DecisionHash { get; }

    /// <summary>Cria uma nova decisão, computando <see cref="DecisionFingerprint"/> e <see cref="DecisionHash"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="assessmentVersion"/> ou <paramref name="decisionVersion"/> não são positivos.</exception>
    /// <exception cref="ArgumentException"><paramref name="itemKey"/>/<paramref name="decidedBy"/>/<paramref name="decidedByRole"/> vazios, ou <paramref name="comment"/> excede o limite/contém caractere de controle.</exception>
    public static ReconciliationExceptionDecision Create(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        int decisionVersion,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion,
        string? comment,
        string decidedBy,
        string decidedByRole,
        CorrelationId correlation,
        DateTimeOffset decidedAtUtc)
    {
        if (assessmentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(assessmentVersion), assessmentVersion, "A versão da avaliação deve ser positiva.");
        }

        if (decisionVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decisionVersion), decisionVersion, "A versão da decisão deve ser positiva.");
        }

        var normalizedItemKey = TextValue.Require(itemKey, nameof(itemKey), maxLength: 320);
        var normalizedDecidedBy = TextValue.Require(decidedBy, nameof(decidedBy), maxLength: 200);
        var normalizedRole = TextValue.Require(decidedByRole, nameof(decidedByRole), maxLength: 50);
        var normalizedComment = ReconciliationExceptionCommentText.Sanitize(comment);

        var canonicalDecidedAt = TruncateToMilliseconds(decidedAtUtc);
        var fingerprint = ComputeDecisionFingerprint(
            tenant, project, wave, plannedJobName, assessmentVersion, itemKind, normalizedItemKey, technicalDisposition,
            status, reasonCode, reasonCodeCatalogVersion, normalizedComment, normalizedDecidedBy);
        var hash = ComputeDecisionHash(
            tenant, project, wave, plannedJobName, assessmentVersion, assessmentSourceFingerprint, itemKind, normalizedItemKey,
            technicalDisposition, decisionVersion, status, reasonCode, reasonCodeCatalogVersion, normalizedComment,
            normalizedDecidedBy, normalizedRole, correlation, canonicalDecidedAt, fingerprint);

        return new ReconciliationExceptionDecision(
            tenant, project, wave, plannedJobName, assessmentVersion, assessmentSourceFingerprint, itemKind, normalizedItemKey,
            technicalDisposition, decisionVersion, status, reasonCode, reasonCodeCatalogVersion, normalizedComment,
            normalizedDecidedBy, normalizedRole, correlation, canonicalDecidedAt, fingerprint, hash);
    }

    /// <summary>
    /// Reconstrói uma decisão JÁ PERSISTIDA (uso exclusivo da camada de persistência), revalidando
    /// <see cref="DecisionFingerprint"/> e <see cref="DecisionHash"/> contra os campos REALMENTE carregados
    /// (fail-closed).
    /// </summary>
    /// <exception cref="ReconciliationIntegrityViolationException">O fingerprint ou o hash persistidos divergem dos recomputados.</exception>
    public static ReconciliationExceptionDecision Rehydrate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        int decisionVersion,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion,
        string? comment,
        string decidedBy,
        string decidedByRole,
        CorrelationId correlation,
        DateTimeOffset decidedAtUtc,
        Sha256Hash persistedFingerprint,
        Sha256Hash persistedHash)
    {
        var recomputedFingerprint = ComputeDecisionFingerprint(
            tenant, project, wave, plannedJobName, assessmentVersion, itemKind, itemKey, technicalDisposition,
            status, reasonCode, reasonCodeCatalogVersion, comment, decidedBy);
        if (!string.Equals(recomputedFingerprint.Value, persistedFingerprint.Value, StringComparison.Ordinal))
        {
            throw new ReconciliationIntegrityViolationException(
                $"O decision_fingerprint persistido para a versão {decisionVersion.ToString(CultureInfo.InvariantCulture)} da " +
                $"decisão sobre o item '{itemKey}' não corresponde ao fingerprint recomputado a partir dos campos carregados " +
                "— decisão possivelmente adulterada ou corrompida.");
        }

        var recomputedHash = ComputeDecisionHash(
            tenant, project, wave, plannedJobName, assessmentVersion, assessmentSourceFingerprint, itemKind, itemKey,
            technicalDisposition, decisionVersion, status, reasonCode, reasonCodeCatalogVersion, comment, decidedBy,
            decidedByRole, correlation, decidedAtUtc, recomputedFingerprint);
        if (!string.Equals(recomputedHash.Value, persistedHash.Value, StringComparison.Ordinal))
        {
            throw new ReconciliationIntegrityViolationException(
                $"O decision_hash persistido para a versão {decisionVersion.ToString(CultureInfo.InvariantCulture)} da " +
                $"decisão sobre o item '{itemKey}' não corresponde ao hash recomputado a partir dos campos carregados " +
                "— decisão possivelmente adulterada ou corrompida.");
        }

        return new ReconciliationExceptionDecision(
            tenant, project, wave, plannedJobName, assessmentVersion, assessmentSourceFingerprint, itemKind, itemKey,
            technicalDisposition, decisionVersion, status, reasonCode, reasonCodeCatalogVersion, comment, decidedBy,
            decidedByRole, correlation, decidedAtUtc, persistedFingerprint, persistedHash);
    }

    /// <summary>
    /// Impressão digital determinística do CONTEÚDO da decisão (item 9), exposta para que a camada de
    /// persistência possa resolver convergência idempotente ANTES de conhecer a versão a alocar (mesmo
    /// padrão de <see cref="ReconciliationAssessment.ComputeSourceFingerprint"/>). Cobre a identidade
    /// completa da exceção, o resultado técnico capturado e o conteúdo decidido (status/motivo/versão do
    /// catálogo/comentário/ator) — NUNCA o papel do ator, a correlação ou o instante da decisão.
    /// </summary>
    public static Sha256Hash ComputeDecisionFingerprint(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion,
        string? comment,
        string decidedBy) =>
        DeterministicHash.Compute(
        [
            "archivebridge.purview.reconciliation-exception-decision-fingerprint.v1",
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plannedJobName.Value,
            assessmentVersion.ToString(CultureInfo.InvariantCulture),
            ((int)itemKind).ToString(CultureInfo.InvariantCulture),
            itemKey,
            ((int)technicalDisposition).ToString(CultureInfo.InvariantCulture),
            ((int)status).ToString(CultureInfo.InvariantCulture),
            ((int)reasonCode).ToString(CultureInfo.InvariantCulture),
            reasonCodeCatalogVersion.ToString(CultureInfo.InvariantCulture),
            comment ?? "null",
            decidedBy,
        ]);

    private static Sha256Hash ComputeDecisionHash(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        int decisionVersion,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion,
        string? comment,
        string decidedBy,
        string decidedByRole,
        CorrelationId correlation,
        DateTimeOffset decidedAtUtc,
        Sha256Hash fingerprint) =>
        DeterministicHash.Compute(
        [
            nameof(ReconciliationExceptionDecision),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plannedJobName.Value,
            assessmentVersion.ToString(CultureInfo.InvariantCulture),
            assessmentSourceFingerprint.Value,
            ((int)itemKind).ToString(CultureInfo.InvariantCulture),
            itemKey,
            ((int)technicalDisposition).ToString(CultureInfo.InvariantCulture),
            decisionVersion.ToString(CultureInfo.InvariantCulture),
            ((int)status).ToString(CultureInfo.InvariantCulture),
            ((int)reasonCode).ToString(CultureInfo.InvariantCulture),
            reasonCodeCatalogVersion.ToString(CultureInfo.InvariantCulture),
            comment ?? "null",
            decidedBy,
            decidedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(decidedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            fingerprint.Value,
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
