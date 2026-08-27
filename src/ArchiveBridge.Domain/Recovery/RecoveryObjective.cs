namespace ArchiveBridge.Domain.Recovery;

/// <summary>
/// Objetivo de recuperação documentado (AB-I7-005 item 2 / runbook §40) que um exercício pode medir.
/// <see cref="None"/> cobre exercícios sem alvo numérico de tempo (ex.: <c>HaFailover</c>,
/// <c>ArtifactEvidenceRecovery</c>). Persistido como <c>TINYINT</c> com o MESMO valor numérico desta enum.
/// </summary>
public enum RecoveryObjective : byte
{
    /// <summary>Nenhum objetivo de tempo aplicável a este tipo de exercício.</summary>
    None = 0,

    /// <summary>Control Plane RTO documentado (&lt;= 4h).</summary>
    ControlPlaneRto = 1,

    /// <summary>Control Plane RPO documentado (&lt;= 5min).</summary>
    ControlPlaneRpo = 2,

    /// <summary>RPO lógico de evidence event confirmado (0 — nenhuma perda lógica tolerada).</summary>
    EvidenceLogicalRpo = 3,
}
