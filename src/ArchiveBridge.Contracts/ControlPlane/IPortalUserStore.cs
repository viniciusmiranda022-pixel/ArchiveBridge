using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>
/// Usuário do portal como PERSISTIDO: identidade, o tenant e o projeto padrão que definem o escopo de
/// leitura (RLS), se está habilitado, o hash da senha e os papéis. O <see cref="Tenant"/>/<see cref="Project"/>
/// deste registro é a ÚNICA fonte do escopo efetivo de dados — o cliente nunca informa tenant como se
/// fosse autorização.
/// </summary>
public sealed record PortalUserRecord(
    Guid UserId,
    string Username,
    string DisplayName,
    TenantId Tenant,
    ProjectId Project,
    bool Enabled,
    PortalPasswordHash Password,
    IReadOnlyList<string> Roles);

/// <summary>Dados para provisionar um novo usuário do portal (bootstrap do admin ou administração futura).</summary>
public sealed record PortalUserRegistration(
    string Username,
    string DisplayName,
    TenantId Tenant,
    ProjectId Project,
    PortalPasswordHash Password,
    IReadOnlyList<string> Roles);

/// <summary>
/// Porta de persistência dos usuários do portal (identidade/acesso). As tabelas de identidade NÃO estão
/// sob a RLS por tenant (são justamente o mecanismo que estabelece o tenant do usuário); ficam protegidas
/// por GRANT à identidade da aplicação e nunca contêm senha em claro. Não é um repositório genérico —
/// expõe apenas o mínimo para autenticar e administrar o acesso ao portal.
/// </summary>
public interface IPortalUserStore
{
    /// <summary>Quantidade de usuários cadastrados (usada pelo bootstrap para saber se o portal está vazio).</summary>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Localiza um usuário pelo login (comparação exata); <see langword="null"/> se inexistente.</summary>
    Task<PortalUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    /// <summary>
    /// Cria um usuário com seus papéis, de forma atômica. Falha (fail-closed) se o login já existir ou se
    /// algum papel for desconhecido — a validação de papel é reforçada tanto aqui quanto por CHECK no banco.
    /// </summary>
    Task CreateAsync(PortalUserRegistration registration, CancellationToken cancellationToken);
}
