using System.Security.Cryptography;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Application.EnterpriseVault.Connector;

/// <summary>Solicitação de emissão — o escopo vem do operador autenticado no composition root, nunca do cliente.</summary>
public sealed record IssueEnrollmentTokenRequest(TenantScope Scope, string RequestedBy, CorrelationId Correlation);

/// <summary>
/// Resultado da emissão: <see cref="RawSecret"/> só existe nesta resposta — nunca é persistido nem pode
/// ser recuperado depois (apenas seu hash fica em <see cref="IEnrollmentTokenStore"/>). O operador deve
/// entregá-lo ao connector por um canal fora de banda; perdê-lo exige nova emissão.
/// </summary>
public sealed record IssueEnrollmentTokenResult(EnrollmentTokenId TokenId, string RawSecret, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Caso de uso operador-iniciado (§15.1, AB-4C-001 critério 1): gera um segredo aleatório de 256 bits,
/// persiste APENAS seu hash SHA-256 e devolve o segredo bruto uma única vez. A janela é sempre
/// <see cref="EnrollmentToken.MaxTtl"/> — nenhum chamador pode pedir uma janela maior.
/// </summary>
public sealed class IssueEnrollmentTokenUseCase(IEnrollmentTokenStore tokens, IClock clock)
{
    private readonly IEnrollmentTokenStore _tokens = tokens;
    private readonly IClock _clock = clock;

    /// <summary>Emite um novo enrollment token para o escopo autenticado.</summary>
    public async Task<IssueEnrollmentTokenResult> ExecuteAsync(
        IssueEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var rawSecret = Convert.ToHexStringLower(secretBytes);
        var tokenHash = DeterministicHash.ComputeBytes(secretBytes);

        var now = _clock.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), request.Scope.Tenant, request.Scope.Project, tokenHash,
            request.RequestedBy, request.Correlation, now, now + EnrollmentToken.MaxTtl);

        await _tokens.IssueAsync(token, cancellationToken).ConfigureAwait(false);
        return new IssueEnrollmentTokenResult(token.Id, rawSecret, token.ExpiresAtUtc);
    }
}
