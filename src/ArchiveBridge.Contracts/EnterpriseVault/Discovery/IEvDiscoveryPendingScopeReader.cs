using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Porta técnica de PROJEÇÃO (somente leitura) que descobre quais escopos tenant/projeto possuem comandos
/// de descoberta EV elegíveis a processamento — sem reivindicar, alterar ou executar nada. É a ÚNICA leitura
/// autorizada a atravessar tenants: a implementação SQL usa a identidade de MANUTENÇÃO para enumerar
/// escopos com trabalho pendente. A partir do <see cref="TenantScope"/> devolvido, todo o processamento volta
/// a usar a identidade normal da aplicação (RLS por tenant + filtro por projeto continuam sendo a autoridade
/// da execução — a enumeração de manutenção NÃO é autorização).
/// </summary>
public interface IEvDiscoveryPendingScopeReader
{
    /// <summary>
    /// Lista, de forma determinística e limitada a <paramref name="max"/>, os escopos DISTINTOS
    /// (tenant/projeto) com pelo menos um Job de descoberta EV elegível (Pending/RetryScheduled com
    /// <c>next_attempt_at</c> vencido/nulo e comando presente). Não devolve site, directory server,
    /// solicitante, hashes, evidência nem qualquer conteúdo — apenas o escopo.
    /// </summary>
    Task<IReadOnlyList<TenantScope>> ListEligibleScopesAsync(int max, CancellationToken cancellationToken);
}
