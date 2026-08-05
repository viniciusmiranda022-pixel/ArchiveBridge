namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>
/// Papéis do Portal Operacional (RBAC). São papéis de APLICAÇÃO — não se confundem com as identidades
/// SQL (<c>ab_app_role</c>/<c>ab_maintenance_role</c>), que continuam sendo o mecanismo concreto de
/// isolamento por tenant no banco. O portal autentica um usuário, resolve seus papéis e autoriza cada
/// tela/ação por papel; toda leitura de dados continua ocorrendo sob a RLS por tenant do usuário.
/// Nesta fatia (Slice 4A) só existem telas de LEITURA e o gating honesto de capacidades ainda não
/// implementadas; as ações de escrita (aprovações, upload de CSV, disparo de descoberta) entram em
/// rodadas seguintes e serão autorizadas por estes mesmos papéis.
/// </summary>
public static class PortalRoles
{
    /// <summary>Consulta somente leitura (dashboard, projetos, ondas, jobs, evidências autorizadas).</summary>
    public const string Viewer = "Viewer";

    /// <summary>Opera o dia a dia da migração (leitura + descoberta READ-ONLY e retry autorizado em rodada futura).</summary>
    public const string Operator = "Operator";

    /// <summary>Aprova/bloqueia projetos e ondas (ações de escrita entram em rodada posterior).</summary>
    public const string Approver = "Approver";

    /// <summary>Somente leitura para auditoria e observabilidade (nunca executa ações).</summary>
    public const string Auditor = "Auditor";

    /// <summary>Administra o portal (usuários/papéis) além de todo o acesso de leitura.</summary>
    public const string Administrator = "Administrator";

    /// <summary>Conjunto canônico de papéis reconhecidos (ordinal, para validação fail-closed).</summary>
    public static readonly IReadOnlyList<string> All = [Viewer, Operator, Approver, Auditor, Administrator];

    /// <summary>Indica se um nome de papel é reconhecido (comparação ordinal, sem cultura).</summary>
    public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);
}
