using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Contracts.EnterpriseVault.Connector;

/// <summary>
/// Token de enrollment inexistente pelo hash informado — indistinguível de expirado/consumido/revogado
/// (anti-enumeração, §15.1). Este é o ÚNICO erro visível para quem apresenta um segredo inválido; as
/// exceções de domínio mais específicas (<see cref="EnrollmentTokenExpiredException"/>,
/// <see cref="EnrollmentTokenNotConsumableException"/>) só existem para um token cujo hash FOI encontrado.
/// </summary>
public sealed class EnrollmentTokenNotFoundException() : Exception("Enrollment token inválido.");

/// <summary>
/// Store de enrollment tokens. O resgate (<see cref="RedeemAsync"/>) é a ÚNICA operação que o connector
/// (ainda sem identidade própria) pode chamar: autentica-se apenas pelo hash do segredo apresentado —
/// nunca por um tenant/projeto informado pelo cliente. A implementação deve resolver, validar e marcar o
/// token como consumido em UMA operação atômica (evita corrida de duplo-resgate concorrente do mesmo token).
/// </summary>
public interface IEnrollmentTokenStore
{
    /// <summary>Persiste um token recém-emitido (cada emissão é um evento distinto; não há idempotência aqui).</summary>
    Task IssueAsync(EnrollmentToken token, CancellationToken cancellationToken);

    /// <summary>
    /// Resgata atomicamente o token pelo hash do segredo apresentado, valida (existência, expiração,
    /// estado) e marca como consumido pelo connector informado, devolvendo o token já no estado
    /// <see cref="EnrollmentTokenStatus.Consumed"/> — o tenant/projeto do connector resultante vêm
    /// inteiramente do token, nunca do chamador.
    /// </summary>
    /// <exception cref="EnrollmentTokenNotFoundException">Hash desconhecido.</exception>
    /// <exception cref="EnrollmentTokenExpiredException">Token encontrado, mas expirado.</exception>
    /// <exception cref="EnrollmentTokenNotConsumableException">Token já consumido (replay) ou revogado.</exception>
    Task<EnrollmentToken> RedeemAsync(
        Sha256Hash tokenHash, ConnectorId connector, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Revoga explicitamente um token dentro do escopo do operador autenticado (ação idempotente).</summary>
    Task RevokeAsync(TenantScope scope, EnrollmentTokenId id, CancellationToken cancellationToken);
}
