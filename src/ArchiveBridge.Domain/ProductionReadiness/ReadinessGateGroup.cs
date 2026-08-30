namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Um dos cinco grupos de gate do Production Readiness Review documentados no runbook §47 (AB-I8-001) —
/// cada valor corresponde a UMA subseção (§47.1-§47.5), nenhum grupo aspiracional adicional. Persistido
/// como <c>TINYINT</c> com o MESMO valor numérico desta enum.
/// </summary>
public enum ReadinessGateGroup : byte
{
    /// <summary>§47.1 — ADRs, diagramas/data flow, capability matrix, ausência de preview em GA, ownership.</summary>
    Architecture = 0,

    /// <summary>§47.2 — threat model, pen-test, secrets scan, SBOM/assinaturas, WDAC/Defender/patching, cross-tenant, incident response.</summary>
    Security = 1,

    /// <summary>§47.3 — hashes/manifests/lineage/WORM, privacy impact assessment, retention/deletion, backup/restore, corpus/fidelity.</summary>
    Data = 2,

    /// <summary>§47.4 — dashboards/alertas, on-call/escalation, DLQ/retry/quarantine, capacity/FinOps, RTO/RPO, support package.</summary>
    Operations = 3,

    /// <summary>§47.5 — roles mínimas, tenant precheck, archive/licença/quota, AzCopy homologado, mapping validator, target root, limites, treinamento.</summary>
    Microsoft365 = 4,
}
