using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Contracts.Jobs;

/// <summary>
/// Escopo de autorização de uma unidade de trabalho: o tenant e o projeto <b>efetivos</b>,
/// derivados do principal autenticado no composition root — <b>nunca</b> de um <c>tenant_id</c>
/// fornecido diretamente pelo cliente como se fosse autorização. Toda operação de Job exige um
/// escopo; a camada durável aplica Row-Level Security definindo <c>SESSION_CONTEXT('tenant_id')</c>
/// com este tenant. Um tenant vazio é rejeitado (fail-closed).
/// </summary>
public readonly record struct TenantScope
{
    /// <summary>Cria um escopo com tenant válido e projeto.</summary>
    /// <exception cref="ArgumentException">Quando o tenant é vazio.</exception>
    public TenantScope(TenantId tenant, ProjectId project)
    {
        if (tenant.Value == Guid.Empty)
        {
            throw new ArgumentException("TenantScope exige um tenant válido (não vazio).", nameof(tenant));
        }

        Tenant = tenant;
        Project = project;
    }

    /// <summary>Tenant efetivo do escopo.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto efetivo do escopo.</summary>
    public ProjectId Project { get; }
}
