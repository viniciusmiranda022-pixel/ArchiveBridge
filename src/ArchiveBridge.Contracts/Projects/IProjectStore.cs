using ArchiveBridge.Contracts.Approvals;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Contracts.Projects;

/// <summary>
/// Porta de persistência do agregado <see cref="MigrationProject"/> (SQL Server on-premises). Cada
/// operação carrega o escopo de tenant/projeto; a RLS por SESSION_CONTEXT impede acesso cruzado e o
/// filtro explícito por project_id isola projetos. Persiste apenas metadados de governança — sem
/// segredo, SAS, token ou conteúdo. Não é um repositório genérico: expõe só o ciclo do projeto.
/// </summary>
public interface IProjectStore
{
    /// <summary>Cria o projeto (e o projeto-base do Slice 1) com a versão de configuração inicial.</summary>
    Task AddAsync(MigrationProject project, CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>Lê o projeto do escopo; <see langword="null"/> se inexistente/de outro tenant.</summary>
    Task<MigrationProject?> GetAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste apenas a transição de estado (status + updated_at) com concorrência otimista por
    /// <c>row_version</c>. Não toca na configuração. Quando <paramref name="fence"/> é informado
    /// (execução durável), valida o cercamento do Job na MESMA transação — um dono defasado não persiste.
    /// </summary>
    Task SaveStatusAsync(
        MigrationProject project, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null);

    /// <summary>
    /// Persiste uma nova versão de configuração: atualiza o agregado e insere a linha imutável de
    /// histórico. Bloqueado pelo domínio e por gatilho quando a configuração está congelada.
    /// </summary>
    Task SaveConfigurationAsync(MigrationProject project, CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste atomicamente a transição de estado (com concorrência otimista) E a linha de decisão
    /// na tabela <c>approvals</c>, em uma única transação. Falha com concorrência se o
    /// <c>row_version</c> divergiu.
    /// </summary>
    Task SaveStatusWithApprovalAsync(MigrationProject project, ApprovalRecord approval, CancellationToken cancellationToken);
}
