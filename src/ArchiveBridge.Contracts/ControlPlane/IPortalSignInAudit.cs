namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>
/// Evento de tentativa de autenticação no portal. Registra SUCESSO e FALHA (fail-closed é auditável):
/// o login tentado, o resultado, um motivo curto e não sensível (ex.: <c>invalid-credentials</c>,
/// <c>disabled</c>, <c>ok</c>), o endereço remoto quando disponível e o instante em UTC. NUNCA contém
/// senha, hash nem qualquer segredo.
/// </summary>
public sealed record PortalSignInEvent(
    string Username,
    bool Succeeded,
    string Reason,
    string? RemoteAddress,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Porta de auditoria de autenticação do portal. As escritas são intrínsecas ao fluxo de login (toda
/// tentativa é registrada, bem ou mal sucedida); a leitura alimenta a trilha de auditoria do portal.
/// </summary>
public interface IPortalSignInAudit
{
    /// <summary>Registra uma tentativa de autenticação (sucesso ou falha).</summary>
    Task RecordAsync(PortalSignInEvent signInEvent, CancellationToken cancellationToken);

    /// <summary>Lê as tentativas mais recentes (ordem decrescente por instante), limitadas a <paramref name="max"/>.</summary>
    Task<IReadOnlyList<PortalSignInEvent>> RecentAsync(int max, CancellationToken cancellationToken);
}
