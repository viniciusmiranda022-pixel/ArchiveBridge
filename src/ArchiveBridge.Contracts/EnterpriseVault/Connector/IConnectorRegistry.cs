using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Contracts.EnterpriseVault.Connector;

/// <summary>Resultado de um registro/reinstalação: a identidade vigente e se foi CRIADA agora (primeira instalação).</summary>
public sealed record ConnectorRegistrationResult(ConnectorIdentity Identity, bool Created);

/// <summary>
/// Store da identidade durável de connectors. O registro é IDEMPOTENTE por (tenant, projeto, thumbprint):
/// reinstalar o MESMO connector converge para a mesma identidade opaca, atualizando apenas campos
/// operacionais mutáveis, sem criar uma segunda identidade nem duplicar evidência (AB-4C-001 critério 6).
/// Leituras escopadas (<see cref="GetAsync"/>) são sempre anti-IDOR: um <see cref="ConnectorId"/> fora do
/// escopo é indistinguível de inexistente.
/// </summary>
public interface IConnectorRegistry
{
    /// <summary>
    /// Registra (primeira instalação) ou converge (reinstalação do mesmo thumbprint/escopo) a identidade
    /// do connector. Reinstalação de um connector já revogado falha fechado
    /// (<see cref="ConnectorRevokedException"/>) — nunca reativa silenciosamente.
    /// </summary>
    Task<ConnectorRegistrationResult> RegisterAsync(ConnectorIdentity identity, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a identidade dentro do escopo autenticado; <see langword="null"/> se inexistente/fora do
    /// escopo (anti-IDOR). Usado tanto por leituras operador-iniciadas quanto por operações
    /// connector-iniciadas — neste último caso, o composition root do transporte (mTLS/workload identity,
    /// fora do escopo deste Passo) é responsável por resolver o <see cref="TenantScope"/> junto da
    /// identidade autenticada do connector ANTES de chamar o caso de uso, exatamente como já ocorre para
    /// principals do Portal (<c>IPortalScopeAccessor</c>) — nunca um escopo informado livremente pelo
    /// próprio connector.
    /// </summary>
    Task<ConnectorIdentity?> GetAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken);

    /// <summary>
    /// Revoga explicitamente a identidade dentro do escopo autenticado (ação idempotente — revogar um
    /// connector já revogado não lança): bloqueia handshakes, inventário, exportação e reinstalação
    /// futuros sem novo enrollment (item 8 de AB-4C-005). Mesmo padrão de
    /// <see cref="IEnrollmentTokenStore.RevokeAsync"/>.
    /// </summary>
    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo (anti-IDOR).</exception>
    Task RevokeAsync(TenantScope scope, ConnectorId connector, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
