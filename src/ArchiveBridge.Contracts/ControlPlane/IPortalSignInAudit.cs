using ArchiveBridge.Domain.IdentityAndAccess;

namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>
/// Evento de tentativa de autenticação no portal. Registra SUCESSO e FALHA (fail-closed é auditável):
/// o alvo quando conhecido (<see cref="TenantId"/>/<see cref="ProjectId"/>/<see cref="UserId"/> — todos
/// <see langword="null"/> quando o usuário é inexistente e a tentativa não pode ser atribuída a um tenant),
/// o login tentado, o resultado, um motivo curto e não sensível (ex.: <c>invalid-credentials</c>,
/// <c>disabled</c>, <c>ok</c>), o endereço remoto quando disponível, o <see cref="CorrelationId"/> da
/// tentativa e o instante em UTC. NUNCA contém senha, hash nem qualquer segredo.
/// </summary>
public sealed record PortalSignInEvent(
    Guid? TenantId,
    Guid? ProjectId,
    Guid? UserId,
    string Username,
    bool Succeeded,
    string Reason,
    string? RemoteAddress,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Porta de auditoria de autenticação do portal. As escritas são intrínsecas ao fluxo de login (toda
/// tentativa é registrada, bem ou mal sucedida); a leitura alimenta a trilha de auditoria do portal e é
/// SEMPRE escopada por tenant (as tabelas de identidade não estão sob a RLS por tenant, então o filtro
/// explícito é o que impede vazamento cross-tenant).
/// </summary>
public interface IPortalSignInAudit
{
    /// <summary>Registra uma tentativa de autenticação (sucesso ou falha).</summary>
    Task RecordAsync(PortalSignInEvent signInEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Lê as tentativas mais recentes DO TENANT informado (ordem decrescente por instante), limitadas a
    /// <paramref name="max"/>. Eventos sem tenant (falhas de usuário inexistente) não pertencem a nenhum
    /// tenant e não aparecem nesta trilha.
    /// </summary>
    Task<IReadOnlyList<PortalSignInEvent>> RecentAsync(TenantId tenant, int max, CancellationToken cancellationToken);
}
