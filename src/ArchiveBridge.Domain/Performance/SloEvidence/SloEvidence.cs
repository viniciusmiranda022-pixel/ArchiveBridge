using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Performance.SloEvidence;

/// <summary>
/// Estado de evidência de UMA métrica de performance/capacidade/SLO (AB-I7-003 §5). Distingue,
/// explicitamente, o que foi de fato medido do que ficou pendente por qualquer motivo — nenhum estado
/// aqui autoriza tratar um cenário não executado como aprovado implicitamente.
/// </summary>
public enum GateStatus
{
    /// <summary>Existe uma <see cref="ObservedMetric"/> real, obtida por execução do harness.</summary>
    Measured,

    /// <summary>Nenhuma medição foi obtida ainda (cenário não executado neste Passo/ambiente).</summary>
    NotMeasured,

    /// <summary>A métrica não se aplica ao domínio/caminho atual (nunca usado para esconder uma lacuna).</summary>
    NotApplicable,

    /// <summary>
    /// Medição bloqueada por dependência externa fora do controle deste harness (ex.: ambiente de cliente,
    /// tenant/EV/M365 real, host dedicado não disponível em CI).
    /// </summary>
    BlockedByExternalDependency,
}

/// <summary>
/// Medição REAL obtida por uma execução do <c>BenchmarkHarness</c> — nunca inventada, nunca inferida de
/// uma estimativa. Sanitizada por construção: apenas nome/valor/unidade numéricos, sem caminho, PII ou
/// segredo (a validação de forma do nome é feita por <see cref="TextValue.Require"/>, mesma política do
/// restante do Domain).
/// </summary>
public sealed record ObservedMetric
{
    /// <summary>Cria uma medição observada, validando forma do nome/unidade e o instante em UTC.</summary>
    /// <exception cref="ArgumentException">Nome/unidade vazios ou valor não finito.</exception>
    public ObservedMetric(string metricName, double value, string unit, DateTimeOffset observedAtUtc)
    {
        MetricName = TextValue.Require(metricName, nameof(metricName), 200);
        Unit = TextValue.Require(unit, nameof(unit), 50);
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("O valor observado precisa ser um número finito.", nameof(value));
        }

        Value = value;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Nome estável da métrica (ex.: <c>HashStreamingThroughputBytesPerSecond</c>).</summary>
    public string MetricName { get; }

    /// <summary>Valor medido.</summary>
    public double Value { get; }

    /// <summary>Unidade do valor (ex.: <c>bytes/s</c>, <c>ms</c>).</summary>
    public string Unit { get; }

    /// <summary>Instante (UTC) em que a medição foi obtida.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>
/// Referência/estimativa citada do runbook (ex.: perfis de worker §46, ~24 GB/dia/mailbox) — NUNCA uma
/// medição própria e NUNCA promovida a SLA por inferência. <see cref="SourceCitation"/> é obrigatória:
/// nenhuma referência sem fonte versionada rastreável.
/// </summary>
public sealed record ReferenceEstimate
{
    /// <summary>Cria uma referência/estimativa citada, exigindo fonte explícita.</summary>
    /// <exception cref="ArgumentException">Nome/unidade/citação vazios ou valor não finito.</exception>
    public ReferenceEstimate(string metricName, double value, string unit, string sourceCitation)
    {
        MetricName = TextValue.Require(metricName, nameof(metricName), 200);
        Unit = TextValue.Require(unit, nameof(unit), 50);
        SourceCitation = TextValue.Require(sourceCitation, nameof(sourceCitation), 500);
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("O valor de referência precisa ser um número finito.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Nome estável da métrica referenciada.</summary>
    public string MetricName { get; }

    /// <summary>Valor de referência/estimativa (típico, não garantido).</summary>
    public double Value { get; }

    /// <summary>Unidade do valor.</summary>
    public string Unit { get; }

    /// <summary>Fonte versionada da estimativa (ex.: caminho + seção do runbook).</summary>
    public string SourceCitation { get; }
}

/// <summary>
/// SLA CONTRATUAL de uma métrica — inexistente neste projeto (AB-I7-003 §5) a menos que uma fonte
/// explicitamente aprovada seja registrada no futuro. A ÚNICA forma de construção pública é
/// <see cref="NotConfigured"/>: não há como um chamador "inventar" um SLA configurado sem uma revisão
/// deste tipo que introduza uma fonte aprovada.
/// </summary>
public sealed record ContractualSla
{
    private const string NotConfiguredStatus = "NOT_CONFIGURED";

    private ContractualSla(string metricName, string status, string? sourceCitation)
    {
        MetricName = metricName;
        Status = status;
        SourceCitation = sourceCitation;
    }

    /// <summary>Nome estável da métrica.</summary>
    public string MetricName { get; }

    /// <summary><c>NOT_CONFIGURED</c> neste projeto — nunca inferido de uma referência/estimativa.</summary>
    public string Status { get; }

    /// <summary>Sempre <see langword="null"/> enquanto <see cref="Status"/> for <c>NOT_CONFIGURED</c>.</summary>
    public string? SourceCitation { get; }

    /// <summary>Marca explicitamente que não existe SLA contratual aprovado para esta métrica.</summary>
    public static ContractualSla NotConfigured(string metricName) =>
        new(TextValue.Require(metricName, nameof(metricName), 200), NotConfiguredStatus, sourceCitation: null);
}

/// <summary>
/// Uma linha da matriz de evidência de SLO/performance/capacidade: liga uma métrica ao seu
/// <see cref="GateStatus"/> e, de acordo com o estado, exatamente à evidência compatível — nunca mistura
/// uma medição real com um estado que declara ausência de medição (fail-closed por construção).
/// </summary>
public sealed record SloEvidenceEntry
{
    /// <summary>Cria uma entrada de evidência, reforçando a correspondência entre estado e payload.</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="GateStatus.Measured"/> sem <see cref="ObservedMetric"/>, ou qualquer outro estado com um
    /// <see cref="ObservedMetric"/> presente, ou <see cref="GateStatus.NotMeasured"/>/
    /// <see cref="GateStatus.BlockedByExternalDependency"/> sem motivo explícito.
    /// </exception>
    public SloEvidenceEntry(
        string metricName,
        GateStatus status,
        ObservedMetric? observed,
        ReferenceEstimate? reference,
        ContractualSla? sla,
        string? blockedOrNotMeasuredReason)
    {
        MetricName = TextValue.Require(metricName, nameof(metricName), 200);
        Status = status;

        if (status == GateStatus.Measured)
        {
            if (observed is null)
            {
                throw new ArgumentException("GateStatus.Measured exige um ObservedMetric.", nameof(observed));
            }
        }
        else if (observed is not null)
        {
            throw new ArgumentException(
                "Somente GateStatus.Measured pode carregar um ObservedMetric (fail-closed: nunca promove ausência de medição a medição).",
                nameof(observed));
        }

        if (status is GateStatus.NotMeasured or GateStatus.BlockedByExternalDependency
            && string.IsNullOrWhiteSpace(blockedOrNotMeasuredReason))
        {
            throw new ArgumentException(
                "NotMeasured/BlockedByExternalDependency exigem um motivo explícito, nunca implícito.",
                nameof(blockedOrNotMeasuredReason));
        }

        Observed = observed;
        Reference = reference;
        Sla = sla;
        Reason = string.IsNullOrWhiteSpace(blockedOrNotMeasuredReason) ? null : blockedOrNotMeasuredReason.Trim();
    }

    /// <summary>Nome estável da métrica.</summary>
    public string MetricName { get; }

    /// <summary>Estado de evidência desta métrica.</summary>
    public GateStatus Status { get; }

    /// <summary>Presente somente quando <see cref="Status"/> é <see cref="GateStatus.Measured"/>.</summary>
    public ObservedMetric? Observed { get; }

    /// <summary>Referência/estimativa do runbook aplicável a esta métrica, se houver.</summary>
    public ReferenceEstimate? Reference { get; }

    /// <summary>SLA contratual aplicável (sempre <c>NOT_CONFIGURED</c> neste projeto, salvo revisão futura).</summary>
    public ContractualSla? Sla { get; }

    /// <summary>Motivo explícito quando não medido/bloqueado; <see langword="null"/> quando medido.</summary>
    public string? Reason { get; }
}
