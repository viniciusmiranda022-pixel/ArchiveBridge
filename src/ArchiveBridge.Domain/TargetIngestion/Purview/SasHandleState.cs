namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Ciclo de vida explícito do material secreto custodiado (work order AB-I5-004 item 9):
/// <c>Stored -&gt; Available -&gt; Consumed | Expired -&gt; Destroyed</c>. Transições são determinísticas e
/// avaliadas por <see cref="PurviewSasUploadHandle"/>; <see cref="Expired"/>/<see cref="Destroyed"/>
/// NUNCA retornam a <see cref="Available"/> sem um novo intake explícito (nova <c>Generation</c>).
/// </summary>
public enum SasHandleState
{
    /// <summary>Segredo protegido e persistido, ainda não confirmado como pronto para aquisição.</summary>
    Stored,

    /// <summary>Pronto para UMA aquisição pelo boundary autorizado (policy de uso único deste Passo).</summary>
    Available,

    /// <summary>Já adquirido uma vez pelo boundary autorizado — nunca readquirível (uso único).</summary>
    Consumed,

    /// <summary>Expirado (janela de validade do SAS ultrapassada) — nunca readquirível.</summary>
    Expired,

    /// <summary>Material local destruído — estado terminal; nenhuma leitura/aquisição possível.</summary>
    Destroyed,
}
