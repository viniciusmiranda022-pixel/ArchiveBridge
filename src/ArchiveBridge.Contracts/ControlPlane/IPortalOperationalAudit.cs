using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>
/// Evento operacional append-only do Portal. O tenant/projeto não vêm do cliente: são fornecidos
/// separadamente pelo <see cref="TenantScope"/> autenticado ao persistir/consultar a trilha. O evento
/// registra apenas metadados técnicos necessários à responsabilização — nunca segredo nem bytes de evidência.
/// </summary>
public sealed record PortalOperationalAuditEvent(
    Guid UserId,
    string Username,
    string ActionCode,
    string ResourceType,
    string ResourceId,
    bool Succeeded,
    string Reason,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Porta da trilha operacional do Portal. Escrita e leitura são sempre escopadas por tenant/projeto;
/// a implementação concreta usa a mesma identidade de aplicação sob RLS e filtro explícito por projeto.
/// </summary>
public interface IPortalOperationalAudit
{
    /// <summary>Registra um evento operacional append-only dentro do escopo autenticado.</summary>
    Task RecordAsync(TenantScope scope, PortalOperationalAuditEvent auditEvent, CancellationToken cancellationToken);

    /// <summary>Retorna os eventos mais recentes do tenant/projeto autenticado, em ordem decrescente.</summary>
    Task<IReadOnlyList<PortalOperationalAuditEvent>> RecentAsync(
        TenantScope scope,
        int max,
        CancellationToken cancellationToken);
}
