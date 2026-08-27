namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Aplicabilidade de UM <see cref="WorkerHardeningControl"/> à baseline on-premises hoje aceita — um eixo
/// DIFERENTE do desfecho de verificação (<see cref="WorkerHardeningStatus"/>): um controle pode ser
/// <see cref="Required"/> e ainda assim <see cref="WorkerHardeningStatus.NotMeasured"/>/
/// <see cref="WorkerHardeningStatus.Blocked"/>. Nunca é informada pelo chamador — é SEMPRE derivada de
/// <see cref="WorkerHardeningBaselineCatalog.Applicability"/>, para que nenhum ator (nem mesmo alegando um
/// papel elevado) possa reclassificar um controle Required como Unsupported ou vice-versa
/// (AB-I7-008 item 1/STOP-THE-LINE).
/// </summary>
public enum WorkerHardeningApplicability : byte
{
    /// <summary>Controle exigido pela baseline on-premises aceita.</summary>
    Required = 0,

    /// <summary>
    /// Controle Azure-only (ou dependente de capability não comprovada) que a baseline on-premises
    /// aceita hoje NÃO assume — nunca tratado como Required sem evidência de capability explícita.
    /// </summary>
    Unsupported = 1,
}
