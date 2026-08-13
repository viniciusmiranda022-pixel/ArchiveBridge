namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Feature gate LOCAL do Portal para a superfície de SOLICITAÇÃO de descoberta EV (a ação de escrita
/// "o Portal pode enfileirar novos Jobs de descoberta?"). É deliberadamente uma opção do próprio Control
/// Plane — NÃO reutiliza <c>ArchiveBridge.Workers.Ev.EnterpriseVaultDiscoveryOptions</c>, para não criar
/// dependência <c>ControlPlane → Workers.Ev</c>. Compartilha apenas o NOME da seção de configuração
/// (<c>EnterpriseVaultDiscovery</c>) por conveniência operacional.
///
/// Este gate NÃO é health check do worker: mesmo com <see cref="Enabled"/> = <see langword="true"/>, quem
/// executa a descoberta é o Worker EV, mais tarde. Deployment operacional deve habilitar Control Plane e
/// Worker de forma COORDENADA (habilitar o disparo no Portal sem um Worker ativo apenas acumula Jobs
/// duráveis pendentes até o Worker subir).
///
/// Fail-closed: o default versionado é <see langword="false"/> — sem habilitação explícita, o Portal não
/// solicita descoberta alguma.
/// </summary>
public sealed class EnterpriseVaultDiscoveryPortalOptions
{
    /// <summary>Nome da seção de configuração (compartilhado por convenção; sem acoplamento de tipos).</summary>
    public const string SectionName = "EnterpriseVaultDiscovery";

    /// <summary>Quando <see langword="true"/>, o Portal pode solicitar (enfileirar) novas descobertas. Default: false.</summary>
    public bool Enabled { get; set; }
}
