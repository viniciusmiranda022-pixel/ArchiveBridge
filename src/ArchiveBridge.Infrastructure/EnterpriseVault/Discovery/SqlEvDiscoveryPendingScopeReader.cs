using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Projeção SQL (somente leitura) dos escopos com trabalho de descoberta EV elegível. Esta é uma das POUCAS
/// operações autorizadas a usar a identidade de MANUTENÇÃO (<see cref="TenantConnectionFactory.OpenForMaintenanceAsync"/>):
/// enumerar escopos exige enxergar múltiplos tenants, o que só a manutenção pode. A consulta é ESTRITAMENTE
/// READ-ONLY (nenhum claim, UPDATE, INSERT, transição, attempt ou efeito de negócio) e devolve apenas o par
/// (tenant, projeto) DISTINTO — nunca site, directory server, solicitante, hashes, evidência, conteúdo ou
/// credenciais. A identidade de manutenção termina AQUI: obtido o <see cref="TenantScope"/>, todo o
/// processamento subsequente volta à identidade normal da aplicação (RLS + filtro por projeto), que continua
/// sendo a autoridade da execução — a enumeração de manutenção NÃO concede autorização.
/// </summary>
public sealed class SqlEvDiscoveryPendingScopeReader(TenantConnectionFactory connectionFactory, IClock clock)
    : IEvDiscoveryPendingScopeReader
{
    private const byte EvWorkload = (byte)Workload.EnterpriseVault;
    private const byte PendingState = (byte)JobState.Pending;
    private const byte RetryScheduledState = (byte)JobState.RetryScheduled;

    // READ-ONLY: DISTINCT (tenant, projeto) dos Jobs EnterpriseVault elegíveis (Pending/RetryScheduled com
    // next_attempt vencido/nulo) que POSSUAM comando de descoberta correspondente (EXISTS). Ordenação
    // determinística por (tenant, projeto); TOP configurável. Nenhuma coluna sensível é selecionada.
    private const string SelectEligibleScopesSql =
        """
        SET NOCOUNT ON;
        SELECT DISTINCT TOP (@max) j.tenant_id, j.project_id
        FROM dbo.jobs j
        WHERE j.workload = @workload
          AND j.state IN (@pending, @retryScheduled)
          AND (j.next_attempt_at_utc IS NULL OR j.next_attempt_at_utc <= @now)
          AND EXISTS (
              SELECT 1 FROM dbo.ev_discovery_commands dc
              WHERE dc.job_id = j.job_id AND dc.tenant_id = j.tenant_id AND dc.project_id = j.project_id)
        ORDER BY j.tenant_id, j.project_id;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantScope>> ListEligibleScopesAsync(int max, CancellationToken cancellationToken)
    {
        if (max <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "O limite de escopos deve ser > 0.");
        }

        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);
        var scopes = new List<TenantScope>();

        // Única leitura autorizada a atravessar tenants; termina antes de qualquer processamento.
        await using var maintenance = await _connectionFactory.OpenForMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectEligibleScopesSql, maintenance.Connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
        command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = EvWorkload });
        command.Parameters.Add(new SqlParameter("@pending", SqlDbType.TinyInt) { Value = PendingState });
        command.Parameters.Add(new SqlParameter("@retryScheduled", SqlDbType.TinyInt) { Value = RetryScheduledState });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scopes.Add(new TenantScope(new TenantId(reader.GetGuid(0)), new ProjectId(reader.GetGuid(1))));
        }

        return scopes;
    }
}
