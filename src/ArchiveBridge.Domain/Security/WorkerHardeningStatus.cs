namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Desfecho de verificação de UM <see cref="WorkerHardeningControl"/> (AB-I7-008 item 1) — mesmo padrão de
/// 3 estados de <see cref="ArchiveBridge.Domain.Recovery.RecoveryReadinessStatus"/>: ausência de evidência
/// NUNCA vira <see cref="Pass"/> por default. A única forma de obter <see cref="Pass"/> é
/// <see cref="WorkerHardeningControlRecord.Pass"/>, que exige uma <see cref="WorkerHardeningMeasurement"/>
/// real E que o controle seja <see cref="WorkerHardeningApplicability.Required"/> — um controle
/// <see cref="WorkerHardeningApplicability.Unsupported"/> nunca pode resultar em <see cref="Pass"/>
/// (bloqueio estrutural, mesmo padrão de <c>HaFailover</c> em <c>RecoveryReadinessRecord</c>). Persistido
/// como <c>TINYINT</c> com o MESMO valor numérico desta enum.
/// </summary>
public enum WorkerHardeningStatus : byte
{
    /// <summary>O controle ainda não foi verificado por nenhuma medição real — fail-closed default.</summary>
    NotMeasured = 0,

    /// <summary>O host/frota não satisfaz o controle, ou a verificação real revelou uma falha.</summary>
    Blocked = 1,

    /// <summary>O controle foi realmente verificado por uma medição real e está conforme.</summary>
    Pass = 2,
}
