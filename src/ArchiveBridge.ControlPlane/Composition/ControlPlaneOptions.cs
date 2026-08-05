namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Opções do host do plano de controle (ligadas da seção <c>ControlPlane</c> da configuração). Instalação
/// ON-PREMISES: sem Azure App Service, sem banco em nuvem, sem SaaS. As strings de conexão vêm de
/// <c>ConnectionStrings</c> e as senhas nunca são versionadas (são injetadas no ambiente de implantação).
/// </summary>
public sealed class ControlPlaneOptions
{
    /// <summary>Seção de configuração.</summary>
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// Executa as migrations no start (conveniência de implantação on-premises). Requer a string de conexão
    /// <c>Migrations</c> (identidade com privilégio de DDL — NUNCA a identidade da aplicação). Padrão: false
    /// (as migrations são um passo de implantação/CI explícito).
    /// </summary>
    public bool RunMigrationsAtStartup { get; set; }

    /// <summary>Provisiona um administrador inicial se o portal estiver vazio (primeiro start on-premises).</summary>
    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();
}

/// <summary>
/// Administrador de bootstrap (primeiro acesso on-premises). Só é criado se o portal não tiver nenhum
/// usuário E a senha estiver definida (via ambiente/secret de implantação, jamais versionada). Se a senha
/// estiver vazia, o bootstrap é ignorado — fail-closed, sem criar credencial fraca.
/// </summary>
public sealed class BootstrapAdminOptions
{
    /// <summary>Login do administrador inicial.</summary>
    public string Username { get; set; } = "admin";

    /// <summary>Nome de exibição.</summary>
    public string DisplayName { get; set; } = "Administrador";

    /// <summary>Senha inicial (injetada no ambiente; vazia = bootstrap desabilitado).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Tenant do administrador (GUID). Define o escopo de leitura sob RLS.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Projeto padrão do administrador (GUID).</summary>
    public string ProjectId { get; set; } = string.Empty;
}
