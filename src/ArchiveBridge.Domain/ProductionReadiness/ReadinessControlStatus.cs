namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Desfecho de UM controle do Production Readiness Review (AB-I8-001, runbook §47) — mesmo padrão de
/// fail-closed já usado por <see cref="ArchiveBridge.Domain.Recovery.RecoveryReadinessStatus"/> e
/// <see cref="ArchiveBridge.Domain.Security.WorkerHardeningStatus"/>: ausência de evidência NUNCA vira
/// <see cref="Pass"/> por default (valor 0 do enum é sempre o estado mais conservador). Diferente daqueles
/// tipos, este enum precisa distinguir cinco desfechos (não três) porque agrega controles heterogêneos:
/// alguns nunca chegam a ser "exercitados" (<see cref="NotPerformed"/>, ex.: pen-test independente), outros
/// são exercitados e falham explicitamente (<see cref="Fail"/>), e outros são bloqueados por limitação
/// arquitetural/processual antes mesmo de tentar (<see cref="Blocked"/>) — a mesma distinção de três estados
/// que <see cref="ArchiveBridge.Domain.Security.PenTestReadinessStatus"/> já usa para pen-test
/// especificamente. Persistido como <c>TINYINT</c> com o MESMO valor numérico desta enum.
/// </summary>
public enum ReadinessControlStatus : byte
{
    /// <summary>Nenhuma evidência foi produzida ainda para este controle — fail-closed default.</summary>
    NotMeasured = 0,

    /// <summary>O controle nunca foi de fato executado/realizado (ex.: pen-test independente nunca contratado).</summary>
    NotPerformed = 1,

    /// <summary>O controle está explicitamente bloqueado por uma limitação arquitetural/processual conhecida.</summary>
    Blocked = 2,

    /// <summary>O controle foi exercitado e o resultado observado não satisfaz o requisito.</summary>
    Fail = 3,

    /// <summary>O controle foi realmente verificado por evidência real e está conforme.</summary>
    Pass = 4,
}
