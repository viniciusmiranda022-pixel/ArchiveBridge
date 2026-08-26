using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Contracts.Performance;

/// <summary>
/// Persistência append-only de execuções de benchmark (AB-I7-003 §1/§9). Tenant/projeto scoped (RLS +
/// filtro explícito por projeto) — nenhuma consulta cruza tenant/projeto por engano. Nunca há operação de
/// atualização/remoção: uma execução registrada é evidência histórica imutável, a base de comparação de
/// <c>PerformanceRegressionComparer</c>.
/// </summary>
public interface IPerformanceBenchmarkResultStore
{
    /// <summary>Persiste uma nova execução de benchmark concluída (append-only).</summary>
    Task<PerformanceBenchmarkRunRecord> SaveAsync(PerformanceBenchmarkRunRecord run, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve até <paramref name="take"/> execuções mais recentes do cenário informado, no escopo
    /// autorizado, da mais recente para a mais antiga — a base para localizar o baseline de comparação.
    /// </summary>
    Task<IReadOnlyList<PerformanceBenchmarkRunRecord>> FindRecentAsync(
        TenantScope scope, string scenarioName, int take, CancellationToken cancellationToken);
}
