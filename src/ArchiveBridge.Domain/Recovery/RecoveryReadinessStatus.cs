namespace ArchiveBridge.Domain.Recovery;

/// <summary>
/// Desfecho de UM exercício de recovery readiness (AB-I7-005 item 9) — <c>Unknown</c>/não medido NUNCA
/// vira <see cref="Pass"/> por default (invariante do work order); a única forma de obter
/// <see cref="Pass"/> é através de <see cref="RecoveryReadinessRecord.Pass"/>, que exige uma
/// <see cref="RecoveryObjectiveMeasurement"/> real e, quando há alvo objetivo, que ele tenha sido
/// atingido. Persistido como <c>TINYINT</c> com o MESMO valor numérico desta enum.
/// </summary>
public enum RecoveryReadinessStatus : byte
{
    /// <summary>A capacidade ainda não foi exercitada por nenhum drill/teste aplicável — fail-closed default.</summary>
    NotMeasured = 0,

    /// <summary>A arquitetura atual não satisfaz a capacidade (ex.: HA sem failover comprovado) ou o exercício revelou falha/objetivo não atingido.</summary>
    Blocked = 1,

    /// <summary>A capacidade foi realmente exercitada por um drill/teste aplicável e o resultado/objetivo foi atingido.</summary>
    Pass = 2,
}
