using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Application.EnterpriseVault.Connector;

/// <summary>
/// Solicitação de registro apresentada pelo connector — nenhum campo de tenant/projeto existe aqui de
/// propósito: o escopo é inteiramente derivado do <see cref="PresentedTokenSecret"/> resgatado.
/// </summary>
public sealed record RegisterConnectorRequest(
    string PresentedTokenSecret,
    ConnectorPublicKeyThumbprint Thumbprint,
    string Hostname,
    string SiteName,
    string ConnectorVersion,
    CorrelationId Correlation);

/// <summary>Resultado do registro: a identidade vigente e se foi CRIADA agora (primeira instalação).</summary>
public sealed record RegisterConnectorResult(ConnectorIdentity Identity, bool Created);

/// <summary>
/// Caso de uso connector-iniciado (§15.1, AB-4C-001 critérios 1-3): resgata o enrollment token
/// (autentica-se SOMENTE pelo hash do segredo apresentado — nunca por um tenant/projeto informado),
/// deriva o escopo inteiramente do token resgatado e registra/converge a identidade do connector.
/// Um segredo malformado (não hexadecimal) é tratado exatamente como um hash desconhecido — mesma
/// resposta indistinguível de "token inválido" (anti-enumeração).
/// </summary>
public sealed class RegisterConnectorUseCase(IEnrollmentTokenStore tokens, IConnectorRegistry connectors, IClock clock)
{
    private readonly IEnrollmentTokenStore _tokens = tokens;
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IClock _clock = clock;

    /// <summary>Resgata o token apresentado e registra/converge a identidade do connector resultante.</summary>
    /// <exception cref="EnrollmentTokenNotFoundException">Segredo malformado ou hash desconhecido.</exception>
    public async Task<RegisterConnectorResult> ExecuteAsync(RegisterConnectorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Sha256Hash tokenHash;
        try
        {
            if (string.IsNullOrWhiteSpace(request.PresentedTokenSecret))
            {
                throw new EnrollmentTokenNotFoundException();
            }

            var secretBytes = Convert.FromHexString(request.PresentedTokenSecret);
            tokenHash = DeterministicHash.ComputeBytes(secretBytes);
        }
        catch (FormatException)
        {
            // Segredo malformado nunca revela mais informação do que um hash simplesmente desconhecido.
            throw new EnrollmentTokenNotFoundException();
        }

        var now = _clock.UtcNow;
        var connectorId = ConnectorId.New();

        // O token resolve o escopo (tenant/projeto) — nunca um valor enviado pelo cliente.
        var redeemed = await _tokens.RedeemAsync(tokenHash, connectorId, now, cancellationToken).ConfigureAwait(false);

        var identity = ConnectorIdentity.Register(
            connectorId, redeemed.Tenant, redeemed.Project, request.Thumbprint,
            request.Hostname, request.SiteName, request.ConnectorVersion, redeemed.Id, now);

        var result = await _connectors.RegisterAsync(identity, cancellationToken).ConfigureAwait(false);
        return new RegisterConnectorResult(result.Identity, result.Created);
    }
}
