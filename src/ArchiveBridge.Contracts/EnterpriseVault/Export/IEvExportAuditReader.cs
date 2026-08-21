using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>
/// Porta de LEITURA da trilha de custódia/auditoria de exportação (mesmo desenho de
/// <c>IJobAuditReader</c>): as escritas já são intrínsecas ao pipeline (<see cref="IEvExportAuditTrail"/>) e
/// atômicas com o efeito de negócio — esta porta expõe apenas a consulta, usada por observabilidade e
/// testes. Nunca conteúdo de mailbox/credencial/transcript bruto (a trilha nunca os contém).
/// </summary>
public interface IEvExportAuditReader
{
    /// <summary>Lê os eventos de custódia/auditoria do pedido, em ordem cronológica.</summary>
    Task<IReadOnlyList<EvExportAuditEvent>> GetEventsAsync(
        TenantScope scope, ExportRequestId request, CancellationToken cancellationToken);
}
