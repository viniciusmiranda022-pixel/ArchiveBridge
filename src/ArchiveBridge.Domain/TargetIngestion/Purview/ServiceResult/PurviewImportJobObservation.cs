using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Evidência IMUTÁVEL e append-only de UMA observação transcrita pelo operador sobre o import job do
/// Purview (runbook §25.9 item 75: "registrar na plataforma o nome/ID do Purview job, operador, horário e
/// screenshot/relatório"). Sempre vinculada a um <see cref="PurviewImportJobPlan"/> JÁ EXISTENTE — o
/// <see cref="ProviderOperationId"/> é evidência OBSERVADA depois da criação humana do job (AB-I6-001 item
/// 5), nunca a chave lógica (que permanece <see cref="PurviewImportJobName"/>). Múltiplas observações da
/// MESMA tentativa registram a progressão observada (Created → ValidationAttached → AnalysisCompleted →
/// ImportStarted → ImportCompleted/ImportFailed) — cada uma é uma linha nova, nunca uma atualização in-place.
/// </summary>
public sealed record PurviewImportJobObservation
{
    /// <summary>Tolerância de relógio para o horário observado não ficar no futuro em relação ao registro.</summary>
    public static readonly TimeSpan FutureClockSkewTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Limite inferior plausível para o horário observado (produto não operava antes disso).</summary>
    public static readonly DateTimeOffset MinPlausibleObservedAtUtc = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private PurviewImportJobObservation(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        PurviewProviderOperationId providerOperationId,
        PurviewImportJobObservedStatus observedStatus,
        DateTimeOffset observedAtUtc,
        string operatorLabel,
        DateTimeOffset recordedAtUtc,
        Sha256Hash observationHash)
    {
        Tenant = tenant;
        Project = project;
        Wave = wave;
        PlannedJobName = plannedJobName;
        ProviderOperationId = providerOperationId;
        ObservedStatus = observedStatus;
        ObservedAtUtc = observedAtUtc;
        OperatorLabel = operatorLabel;
        RecordedAtUtc = recordedAtUtc;
        ObservationHash = observationHash;
    }

    /// <summary>Tenant do escopo autorizado.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado.</summary>
    public ProjectId Project { get; }

    /// <summary>Onda vinculada.</summary>
    public WaveId Wave { get; }

    /// <summary>Nome planejado do plano ao qual esta observação pertence.</summary>
    public PurviewImportJobName PlannedJobName { get; }

    /// <summary>ID/nome do Purview job observado pelo operador (evidência, nunca a chave lógica).</summary>
    public PurviewProviderOperationId ProviderOperationId { get; }

    /// <summary>Ponto de progresso observado — nunca conclui a onda/projeto (ver <see cref="PurviewImportJobObservedStatus"/>).</summary>
    public PurviewImportJobObservedStatus ObservedStatus { get; }

    /// <summary>Horário observado pelo operador no portal (não o horário de registro).</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Identificador sanitizado do operador que registrou a observação (evidência/auditoria).</summary>
    public string OperatorLabel { get; }

    /// <summary>Instante em que a observação foi registrada no ArchiveBridge (relógio do servidor).</summary>
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>Hash determinístico de todos os campos persistidos (detecta adulteração de qualquer um deles).</summary>
    public Sha256Hash ObservationHash { get; }

    /// <summary>
    /// Cria uma nova observação, validando forma dos campos e que <paramref name="observedAtUtc"/> está
    /// dentro de limites plausíveis em relação a <paramref name="recordedAtUtc"/> (fail-closed contra
    /// entrada absurda/adulterada do formulário).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="operatorLabel"/> é vazio/inválido.</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException"><paramref name="observedAtUtc"/> fora dos limites plausíveis.</exception>
    public static PurviewImportJobObservation Create(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        PurviewProviderOperationId providerOperationId,
        PurviewImportJobObservedStatus observedStatus,
        DateTimeOffset observedAtUtc,
        string operatorLabel,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(providerOperationId);
        var label = TextValue.Require(operatorLabel, nameof(operatorLabel), 200);
        var canonicalObservedAt = TruncateToMilliseconds(observedAtUtc);
        var canonicalRecordedAt = TruncateToMilliseconds(recordedAtUtc);

        if (canonicalObservedAt < MinPlausibleObservedAtUtc || canonicalObservedAt > canonicalRecordedAt + FutureClockSkewTolerance)
        {
            throw new PurviewImportJobPrerequisiteException(
                "O horário observado está fora dos limites plausíveis em relação ao registro (fail-closed).");
        }

        var hash = ComputeObservationHash(
            tenant, project, wave, plannedJobName, providerOperationId, observedStatus, canonicalObservedAt, label, canonicalRecordedAt);
        return new PurviewImportJobObservation(
            tenant, project, wave, plannedJobName, providerOperationId, observedStatus, canonicalObservedAt, label, canonicalRecordedAt, hash);
    }

    /// <summary>
    /// Reconstrói uma observação JÁ PERSISTIDA (uso exclusivo da camada de persistência), revalidando
    /// <see cref="ObservationHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="PurviewImportJobIntegrityViolationException">O hash persistido diverge do recomputado.</exception>
    public static PurviewImportJobObservation Rehydrate(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        PurviewProviderOperationId providerOperationId,
        PurviewImportJobObservedStatus observedStatus,
        DateTimeOffset observedAtUtc,
        string operatorLabel,
        DateTimeOffset recordedAtUtc,
        Sha256Hash persistedObservationHash)
    {
        var recomputed = ComputeObservationHash(
            tenant, project, wave, plannedJobName, providerOperationId, observedStatus, observedAtUtc, operatorLabel, recordedAtUtc);
        if (!string.Equals(recomputed.Value, persistedObservationHash.Value, StringComparison.Ordinal))
        {
            throw new PurviewImportJobIntegrityViolationException(
                $"O observation_hash persistido para o plano {plannedJobName.Value} não corresponde ao hash recomputado " +
                "a partir dos campos carregados — observação possivelmente adulterada ou corrompida.");
        }

        return new PurviewImportJobObservation(
            tenant, project, wave, plannedJobName, providerOperationId, observedStatus, observedAtUtc, operatorLabel, recordedAtUtc, persistedObservationHash);
    }

    /// <summary>
    /// Verdadeiro quando <paramref name="other"/> representa exatamente a MESMA observação lógica (mesmo
    /// plano, provider ID, status e horário observado) — usado para convergência idempotente de replay
    /// (AB-I6-001 item 10): nunca compara <see cref="RecordedAtUtc"/>/<see cref="OperatorLabel"/>, que
    /// podem variar entre uma chamada e seu replay sem que o CONTEÚDO observado tenha mudado.
    /// </summary>
    public bool IsSameLogicalObservationAs(PurviewImportJobObservation other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Tenant == other.Tenant
            && Project == other.Project
            && Wave == other.Wave
            && PlannedJobName.Equals(other.PlannedJobName)
            && string.Equals(ProviderOperationId.Value, other.ProviderOperationId.Value, StringComparison.Ordinal)
            && ObservedStatus == other.ObservedStatus
            && ObservedAtUtc == other.ObservedAtUtc;
    }

    private static Sha256Hash ComputeObservationHash(
        TenantId tenant,
        ProjectId project,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        PurviewProviderOperationId providerOperationId,
        PurviewImportJobObservedStatus observedStatus,
        DateTimeOffset observedAtUtc,
        string operatorLabel,
        DateTimeOffset recordedAtUtc) =>
        DeterministicHash.Compute(
        [
            nameof(PurviewImportJobObservation),
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            wave.Value.ToString("N"),
            plannedJobName.Value,
            providerOperationId.Value,
            ((int)observedStatus).ToString(CultureInfo.InvariantCulture),
            TruncateToMilliseconds(observedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
            operatorLabel,
            TruncateToMilliseconds(recordedAtUtc).UtcTicks.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
