using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Evidência IMUTÁVEL e append-only da verificação de UM <see cref="WorkerHardeningControl"/> da baseline
/// de workers Windows (AB-I7-008 item 1) — tenant/project-scoped, tamper-evident e determinística. Mesmo
/// desenho de <see cref="ArchiveBridge.Domain.Recovery.RecoveryReadinessRecord"/>: versionamento monotônico
/// por (tenant, project, controle), convergência idempotente por <see cref="ContentFingerprint"/>, e
/// <see cref="Rehydrate"/> revalida <see cref="RecordHash"/> contra os campos REALMENTE carregados
/// (persistência é fronteira NÃO CONFIÁVEL).
/// <para>
/// A ÚNICA forma de obter <see cref="WorkerHardeningStatus.Pass"/> é <see cref="Pass"/>, que exige uma
/// <see cref="WorkerHardeningMeasurement"/> REAL — nenhum código deste ambiente pode fabricar essa medição
/// fora de um teste que a constrói explicitamente, então nenhuma evidência real de host Windows existe
/// hoje: a baseline permanece <see cref="WorkerHardeningStatus.NotMeasured"/>/
/// <see cref="WorkerHardeningStatus.Blocked"/> neste Passo. Além disso, um controle cuja
/// <see cref="WorkerHardeningApplicability"/> (SEMPRE derivada de
/// <see cref="WorkerHardeningBaselineCatalog"/>, nunca informada pelo chamador) é
/// <see cref="WorkerHardeningApplicability.Unsupported"/> NUNCA pode resultar em <see cref="Pass"/> —
/// bloqueio estrutural, mesmo padrão de <c>HaFailover</c> em <c>RecoveryReadinessRecord</c>.
/// </para>
/// </summary>
public sealed record WorkerHardeningControlRecord
{
    /// <summary>Prefixo versionado do schema deste registro — gravado em toda versão nova, nunca reescrito.</summary>
    public const string CurrentSchemaVersion = "archivebridge.security.worker-hardening-record.v1";

    private const int MaxTextLength = 1000;

    /// <summary>Fingerprint canônico quando nenhuma evidência foi produzida ainda (<see cref="NotMeasured"/>).</summary>
    public static readonly Sha256Hash NoEvidenceFingerprint =
        DeterministicHash.Compute(["archivebridge.security.worker-hardening-record.no-evidence.v1"]);

    private WorkerHardeningControlRecord(
        TenantId tenant,
        ProjectId project,
        WorkerHardeningControl control,
        int controlVersion,
        WorkerHardeningApplicability applicability,
        WorkerHardeningStatus status,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc,
        string schemaVersion,
        Sha256Hash contentFingerprint,
        Sha256Hash recordHash)
    {
        Tenant = tenant;
        Project = project;
        Control = control;
        ControlVersion = controlVersion;
        Applicability = applicability;
        Status = status;
        Measurement = measurement;
        EvidenceFingerprint = evidenceFingerprint;
        BlockedReason = blockedReason;
        Notes = notes;
        ExecutedBy = executedBy;
        ExecutedByRole = executedByRole;
        Correlation = correlation;
        ExecutedAtUtc = executedAtUtc;
        SchemaVersion = schemaVersion;
        ContentFingerprint = contentFingerprint;
        RecordHash = recordHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Controle de hardening verificado por este registro.</summary>
    public WorkerHardeningControl Control { get; }

    /// <summary>Versão monotônica (1..N) deste registro dentro de (tenant, project, controle).</summary>
    public int ControlVersion { get; }

    /// <summary>Aplicabilidade FIXA do controle nesta baseline — sempre derivada do catálogo, nunca informada pelo chamador.</summary>
    public WorkerHardeningApplicability Applicability { get; }

    /// <summary>Desfecho canônico da verificação — nunca <see cref="WorkerHardeningStatus.Pass"/> sem medição real.</summary>
    public WorkerHardeningStatus Status { get; }

    /// <summary>Medição REAL da verificação — presente se e somente se o controle foi de fato verificado.</summary>
    public WorkerHardeningMeasurement? Measurement { get; }

    /// <summary>Hash determinístico da evidência canônica subjacente (ex.: saída de uma consulta de política local) — nunca o conteúdo bruto.</summary>
    public Sha256Hash EvidenceFingerprint { get; }

    /// <summary>Motivo documentado quando <see cref="Status"/> é <see cref="WorkerHardeningStatus.Blocked"/> sem medição (limitação arquitetural/hardware).</summary>
    public string BlockedReason { get; }

    /// <summary>Notas técnicas objetivas — nunca segredo/PII/caminho sensível.</summary>
    public string Notes { get; }

    /// <summary>Ator server-side responsável pela verificação.</summary>
    public string ExecutedBy { get; }

    /// <summary>Papel RBAC alegado do ator no instante da verificação — NUNCA usado para alterar <see cref="Applicability"/> ou autorizar <see cref="Pass"/> sem medição real.</summary>
    public string ExecutedByRole { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante em que ESTA versão foi persistida (append-only — nunca mutado depois).</summary>
    public DateTimeOffset ExecutedAtUtc { get; }

    /// <summary>Versão do schema deste registro.</summary>
    public string SchemaVersion { get; }

    /// <summary>Impressão digital determinística do RESULTADO da verificação — usada para convergência idempotente; nunca cobre versão/timestamp/ator.</summary>
    public Sha256Hash ContentFingerprint { get; }

    /// <summary>Hash determinístico de TODOS os campos persistidos — recomputado e validado fail-closed em toda leitura.</summary>
    public Sha256Hash RecordHash { get; }

    /// <summary>Registra um controle REALMENTE verificado e conforme.</summary>
    /// <exception cref="WorkerHardeningInvariantViolationException">
    /// O controle é <see cref="WorkerHardeningApplicability.Unsupported"/> nesta baseline (nunca pode ser Pass).
    /// </exception>
    public static WorkerHardeningControlRecord Pass(
        TenantId tenant,
        ProjectId project,
        WorkerHardeningControl control,
        int controlVersion,
        WorkerHardeningMeasurement measurement,
        Sha256Hash evidenceFingerprint,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc)
    {
        if (WorkerHardeningBaselineCatalog.Applicability(control) == WorkerHardeningApplicability.Unsupported)
        {
            throw new WorkerHardeningInvariantViolationException(
                $"O controle {control} é Unsupported nesta baseline on-premises aceita — nenhum papel/ator pode " +
                "declará-lo Pass (bloqueio estrutural, mesmo padrão de HaFailover em RecoveryReadinessRecord).");
        }

        return Create(
            tenant, project, control, controlVersion, WorkerHardeningStatus.Pass, measurement, evidenceFingerprint,
            blockedReason: string.Empty, notes, executedBy, executedByRole, correlation, executedAtUtc);
    }

    /// <summary>Registra um controle explicitamente bloqueado — sem conformidade comprovada (com ou sem medição real).</summary>
    /// <exception cref="ArgumentException"><paramref name="blockedReason"/> vazio quando não há medição.</exception>
    public static WorkerHardeningControlRecord Blocked(
        TenantId tenant,
        ProjectId project,
        WorkerHardeningControl control,
        int controlVersion,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc) =>
        Create(
            tenant, project, control, controlVersion, WorkerHardeningStatus.Blocked, measurement, evidenceFingerprint,
            blockedReason, notes, executedBy, executedByRole, correlation, executedAtUtc);

    /// <summary>Registra que o controle ainda não foi verificado por nenhuma medição real — fail-closed default, nunca <see cref="WorkerHardeningStatus.Pass"/>.</summary>
    public static WorkerHardeningControlRecord NotMeasured(
        TenantId tenant,
        ProjectId project,
        WorkerHardeningControl control,
        int controlVersion,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc) =>
        Create(
            tenant, project, control, controlVersion, WorkerHardeningStatus.NotMeasured, measurement: null,
            NoEvidenceFingerprint, blockedReason: string.Empty, notes, executedBy, executedByRole, correlation, executedAtUtc);

    private static WorkerHardeningControlRecord Create(
        TenantId tenant,
        ProjectId project,
        WorkerHardeningControl control,
        int controlVersion,
        WorkerHardeningStatus status,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc)
    {
        if (controlVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "A versão do controle deve ser positiva.");
        }

        if (status == WorkerHardeningStatus.Pass && measurement is null)
        {
            throw new WorkerHardeningInvariantViolationException(
                "Pass exige uma WorkerHardeningMeasurement real — não é possível declarar conformidade sem verificação.");
        }

        if (status == WorkerHardeningStatus.NotMeasured && measurement is not null)
        {
            throw new ArgumentException("NotMeasured não pode carregar uma medição — o controle ainda não foi verificado.", nameof(measurement));
        }

        var normalizedBlockedReason = EvidenceText.RequireSafeOptional(
            blockedReason, nameof(blockedReason), MaxTextLength, m => new WorkerHardeningInvariantViolationException(
                $"{m} aparenta conter um segredo/PII — recusado por design (fail-closed)."));

        if (status == WorkerHardeningStatus.Blocked && measurement is null && normalizedBlockedReason.Length == 0)
        {
            throw new ArgumentException(
                "Blocked sem medição (limitação arquitetural/hardware) exige blockedReason documentado.", nameof(blockedReason));
        }

        var normalizedNotes = EvidenceText.RequireSafeOptional(
            notes, nameof(notes), MaxTextLength, m => new WorkerHardeningInvariantViolationException(
                $"{m} aparenta conter um segredo/PII — recusado por design (fail-closed)."));
        var normalizedExecutedBy = TextValue.Require(executedBy, nameof(executedBy), maxLength: 200);
        var normalizedExecutedByRole = TextValue.Require(executedByRole, nameof(executedByRole), maxLength: 50);
        var canonicalExecutedAt = TruncateToMilliseconds(executedAtUtc);
        var applicability = WorkerHardeningBaselineCatalog.Applicability(control);

        // Mesmo tratamento de canonicalização que executedAtUtc: a coluna persistida
        // (measurement_measured_at_utc DATETIME2(3)) só guarda precisão de milissegundo, então o instante
        // real da medição (tipicamente DateTimeOffset.UtcNow, com componente sub-milissegundo) é truncado
        // ANTES de virar o valor canônico do registro — nunca depois. Sem isto, o valor gravado no SQL
        // Server (arredondado pelo próprio driver/engine ao inserir um valor não alinhado) divergiria do
        // valor usado aqui no fingerprint, disparando WorkerHardeningIntegrityViolationException
        // falso-positivo em toda leitura real.
        var canonicalMeasurement = measurement is { } rawMeasurement
            ? new WorkerHardeningMeasurement(TruncateToMilliseconds(rawMeasurement.MeasuredAtUtc), rawMeasurement.MeasurementMethod)
            : (WorkerHardeningMeasurement?)null;

        var fingerprint = ComputeContentFingerprint(status, canonicalMeasurement, evidenceFingerprint, normalizedBlockedReason, normalizedNotes);
        var hash = ComputeRecordHash(
            tenant, project, control, controlVersion, applicability, fingerprint, normalizedExecutedBy,
            normalizedExecutedByRole, correlation, canonicalExecutedAt, CurrentSchemaVersion);

        return new WorkerHardeningControlRecord(
            tenant, project, control, controlVersion, applicability, status, canonicalMeasurement, evidenceFingerprint,
            normalizedBlockedReason, normalizedNotes, normalizedExecutedBy, normalizedExecutedByRole, correlation,
            canonicalExecutedAt, CurrentSchemaVersion, fingerprint, hash);
    }

    /// <summary>Reconstrói um registro JÁ PERSISTIDO, revalidando <see cref="ContentFingerprint"/> e <see cref="RecordHash"/> contra os campos REALMENTE carregados (fail-closed).</summary>
    /// <exception cref="WorkerHardeningIntegrityViolationException">Fingerprint/hash persistidos divergem dos recomputados.</exception>
    public static WorkerHardeningControlRecord Rehydrate(
        TenantId tenant,
        ProjectId project,
        WorkerHardeningControl control,
        int controlVersion,
        WorkerHardeningStatus status,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc,
        string schemaVersion,
        Sha256Hash persistedContentFingerprint,
        Sha256Hash persistedRecordHash)
    {
        var applicability = WorkerHardeningBaselineCatalog.Applicability(control);

        var recomputedFingerprint = ComputeContentFingerprint(status, measurement, evidenceFingerprint, blockedReason, notes);
        if (!string.Equals(recomputedFingerprint.Value, persistedContentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new WorkerHardeningIntegrityViolationException(
                $"O content_fingerprint persistido para a versão {controlVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"do controle {control} não corresponde ao fingerprint recomputado — registro possivelmente adulterado ou corrompido.");
        }

        var recomputedHash = ComputeRecordHash(
            tenant, project, control, controlVersion, applicability, persistedContentFingerprint, executedBy,
            executedByRole, correlation, executedAtUtc, schemaVersion);
        if (!string.Equals(recomputedHash.Value, persistedRecordHash.Value, StringComparison.Ordinal))
        {
            throw new WorkerHardeningIntegrityViolationException(
                $"O record_hash persistido para a versão {controlVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"do controle {control} não corresponde ao hash recomputado — registro possivelmente adulterado ou corrompido.");
        }

        return new WorkerHardeningControlRecord(
            tenant, project, control, controlVersion, applicability, status, measurement, evidenceFingerprint,
            blockedReason, notes, executedBy, executedByRole, correlation, executedAtUtc, schemaVersion,
            persistedContentFingerprint, persistedRecordHash);
    }

    /// <summary>Impressão digital determinística do RESULTADO da verificação — exposta para que a store resolva convergência idempotente antes de conhecer a versão a alocar.</summary>
    /// <remarks>
    /// <see cref="WorkerHardeningMeasurement.MeasuredAtUtc"/> é truncado para precisão de milissegundo antes
    /// de entrar no fingerprint — a coluna persistida (<c>measurement_measured_at_utc DATETIME2(3)</c>) só
    /// guarda essa precisão; sem truncar aqui, uma medição real com componente sub-milissegundo (comum em
    /// <see cref="DateTimeOffset.UtcNow"/>) produziria um fingerprint que NUNCA sobrevive a um round-trip real
    /// pelo SQL Server, disparando <see cref="WorkerHardeningIntegrityViolationException"/> falso-positivo em
    /// toda leitura — o mesmo padrão já aplicado a <c>executedAtUtc</c> nesta classe.
    /// </remarks>
    public static Sha256Hash ComputeContentFingerprint(
        WorkerHardeningStatus status,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes) =>
        DeterministicHash.Compute(
        [
            "archivebridge.security.worker-hardening-fingerprint.v1",
            ((int)status).ToString(CultureInfo.InvariantCulture),
            measurement is { } m ? TruncateToMilliseconds(m.MeasuredAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture) : "none",
            measurement?.MeasurementMethod ?? "none",
            evidenceFingerprint.Value,
            blockedReason,
            notes,
        ]);

    private static Sha256Hash ComputeRecordHash(
        TenantId tenant,
        ProjectId project,
        WorkerHardeningControl control,
        int controlVersion,
        WorkerHardeningApplicability applicability,
        Sha256Hash contentFingerprint,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset executedAtUtc,
        string schemaVersion) =>
        DeterministicHash.Compute(
        [
            nameof(WorkerHardeningControlRecord),
            schemaVersion,
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            ((int)control).ToString(CultureInfo.InvariantCulture),
            controlVersion.ToString(CultureInfo.InvariantCulture),
            ((int)applicability).ToString(CultureInfo.InvariantCulture),
            contentFingerprint.Value,
            executedBy,
            executedByRole,
            correlation.Value.ToString("N"),
            TruncateToMilliseconds(executedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
