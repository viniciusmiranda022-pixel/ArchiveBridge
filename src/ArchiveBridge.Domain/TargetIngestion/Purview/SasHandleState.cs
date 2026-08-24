namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Ciclo de vida explícito do material secreto custodiado (work order AB-I5-004 item 9, revisado por
/// AB-I5-006 item 2/3): <c>Stored -&gt; Available -&gt; Claimed -&gt; Consumed | Expired -&gt; Destroyed</c>.
/// Transições são determinísticas e avaliadas por <see cref="PurviewSasUploadHandle"/>;
/// <see cref="Expired"/>/<see cref="Destroyed"/> NUNCA retornam a <see cref="Available"/> sem um novo
/// intake explícito (nova <c>Generation</c>). Valores numéricos fixados explicitamente — são persistidos
/// como <c>TINYINT</c> (migration 0027); <see cref="Claimed"/> foi adicionado no final da faixa (5) para
/// nunca renumerar os estados já usados pelos demais.
/// </summary>
public enum SasHandleState : byte
{
    /// <summary>Segredo protegido e persistido, ainda não confirmado como pronto para aquisição.</summary>
    Stored = 0,

    /// <summary>Pronto para UMA reivindicação (<see cref="PurviewSasUploadHandle.Claim"/>) pelo boundary autorizado.</summary>
    Available = 1,

    /// <summary>Já adquirido uma vez pelo boundary autorizado — nunca readquirível (uso único).</summary>
    Consumed = 2,

    /// <summary>Expirado (janela de validade do SAS ultrapassada) — nunca readquirível.</summary>
    Expired = 3,

    /// <summary>Material local destruído — estado terminal; nenhuma leitura/aquisição possível.</summary>
    Destroyed = 4,

    /// <summary>
    /// Reservado por UM adquirente sob lease/fencing (<see cref="PurviewSasUploadHandle.ClaimEpoch"/>,
    /// AB-I5-006 item 2) — ainda não finalizado. Um lease expirado torna o handle elegível a
    /// <see cref="PurviewSasUploadHandle.Reclaim"/> por um novo adquirente, SEM queimar a geração; a
    /// finalização (<see cref="PurviewSasUploadHandle.FinalizeClaim"/>) só é aceita sob a época titular.
    /// </summary>
    Claimed = 5,
}
