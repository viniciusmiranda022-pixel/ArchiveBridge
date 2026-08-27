namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Read-model puro que agrega o status VIGENTE de cada categoria de evidência de segurança deste Passo
/// (AB-I7-008 item 7) — nunca deriva/persiste nada por si só; recebe os valores já resolvidos pelo
/// chamador (tipicamente a partir dos stores de cada categoria) e apenas os compõe. Não define, não
/// deriva e não expõe NENHUM status agregado de "Production Ready"/"GoLive"/"canário aprovado" — o único
/// texto normativo que este tipo expõe é <see cref="Disclaimer"/>, que afirma explicitamente o contrário
/// (STOP-THE-LINE do work order).
/// </summary>
public sealed record SecurityReadinessSnapshot
{
    /// <summary>
    /// Disclaimer FIXO exposto por toda leitura deste snapshot — este agregado NUNCA certifica
    /// "Production Ready"/"GoLive"/canário aprovado nem um pen-test independente concluído.
    /// </summary>
    public const string Disclaimer =
        "This snapshot reports only the latest verification status of the individual security controls " +
        "actually exercised by AB-I7-008 (I7 Hardening, Passo 4). It NEVER certifies overall Production " +
        "Readiness, GoLive/canary approval, or an independent penetration test, and this type defines and " +
        "exposes no such overall status.";

    private SecurityReadinessSnapshot(
        IReadOnlyDictionary<WorkerHardeningControl, WorkerHardeningStatus> workerHardeningControls,
        WdacPolicyEvidence? latestWdacPolicy,
        IReadOnlyList<BuildProvenanceRecord> latestBuildProvenanceByArtifact,
        IReadOnlyDictionary<IncidentResponseDrillType, IncidentResponseDrillOutcome> latestIncidentResponseDrills,
        PenTestReadinessStatus penTestStatus)
    {
        WorkerHardeningControls = workerHardeningControls;
        LatestWdacPolicy = latestWdacPolicy;
        LatestBuildProvenanceByArtifact = latestBuildProvenanceByArtifact;
        LatestIncidentResponseDrills = latestIncidentResponseDrills;
        PenTestStatus = penTestStatus;
    }

    /// <summary>Status vigente por controle de hardening de worker — ausente do dicionário equivale a <see cref="WorkerHardeningStatus.NotMeasured"/>.</summary>
    public IReadOnlyDictionary<WorkerHardeningControl, WorkerHardeningStatus> WorkerHardeningControls { get; }

    /// <summary>Versão vigente da policy WDAC/App Control, se já emitida.</summary>
    public WdacPolicyEvidence? LatestWdacPolicy { get; }

    /// <summary>Build aprovada vigente por artifact.</summary>
    public IReadOnlyList<BuildProvenanceRecord> LatestBuildProvenanceByArtifact { get; }

    /// <summary>Desfecho vigente por tipo de drill de incident-response — ausente do dicionário equivale a "ainda não exercitado".</summary>
    public IReadOnlyDictionary<IncidentResponseDrillType, IncidentResponseDrillOutcome> LatestIncidentResponseDrills { get; }

    /// <summary>Status vigente de preparação para pen-test — nunca um valor que implique conclusão real (o tipo não possui tal caso).</summary>
    public PenTestReadinessStatus PenTestStatus { get; }

    /// <summary>Compõe o snapshot a partir dos valores JÁ RESOLVIDOS pelo chamador (nenhum acesso a store aqui).</summary>
    public static SecurityReadinessSnapshot Compose(
        IReadOnlyDictionary<WorkerHardeningControl, WorkerHardeningStatus> workerHardeningControls,
        WdacPolicyEvidence? latestWdacPolicy,
        IReadOnlyList<BuildProvenanceRecord> latestBuildProvenanceByArtifact,
        IReadOnlyDictionary<IncidentResponseDrillType, IncidentResponseDrillOutcome> latestIncidentResponseDrills,
        PenTestReadinessStatus penTestStatus)
    {
        ArgumentNullException.ThrowIfNull(workerHardeningControls);
        ArgumentNullException.ThrowIfNull(latestBuildProvenanceByArtifact);
        ArgumentNullException.ThrowIfNull(latestIncidentResponseDrills);

        return new SecurityReadinessSnapshot(
            new Dictionary<WorkerHardeningControl, WorkerHardeningStatus>(workerHardeningControls),
            latestWdacPolicy,
            [.. latestBuildProvenanceByArtifact],
            new Dictionary<IncidentResponseDrillType, IncidentResponseDrillOutcome>(latestIncidentResponseDrills),
            penTestStatus);
    }
}
