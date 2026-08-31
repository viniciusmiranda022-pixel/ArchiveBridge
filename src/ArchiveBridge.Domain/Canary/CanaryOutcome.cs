namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Desfecho agregado do canário de produção (AB-I8-004). DELIBERADAMENTE possui apenas estes dois valores —
/// não existe, e nunca deve existir, um caso <c>ProductionReady</c>/<c>GoLive</c>/<c>Completed</c>
/// (STOP-THE-LINE do work order: este Passo nunca declara go-live nem marca projeto/wave concluído).
/// <see cref="NotPassed"/> é o default fail-closed (valor 0).
/// </summary>
public enum CanaryOutcome : byte
{
    /// <summary>Pelo menos um cenário obrigatório aplicável não está <see cref="CanaryScenarioStatus.Pass"/> — fail-closed default.</summary>
    NotPassed = 0,

    /// <summary>
    /// TODOS os cenários obrigatórios do catálogo (incluindo o gate de aprovação da primeira onda) estão
    /// <see cref="CanaryScenarioStatus.Pass"/>. Mesmo neste estado, este tipo NUNCA representa
    /// <c>ProductionReady</c>/<c>GoLive</c>/projeto <c>COMPLETED</c> — apenas que o canário controlado
    /// satisfez integralmente o §48; os critérios de encerramento de migração do §49 continuam fora do
    /// escopo executável deste Passo.
    /// </summary>
    CanaryPassed = 1,
}
