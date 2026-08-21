using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>Identidade do enrollment token, gerada pelo servidor na emissão.</summary>
public readonly record struct EnrollmentTokenId(Guid Value)
{
    /// <summary>Gera uma nova identidade de token.</summary>
    public static EnrollmentTokenId New() => new(Guid.NewGuid());
}

/// <summary>Estado do enrollment token — uso único (§15.1).</summary>
public enum EnrollmentTokenStatus
{
    /// <summary>Emitido, ainda não resgatado.</summary>
    Issued,

    /// <summary>Resgatado exatamente uma vez por um connector.</summary>
    Consumed,

    /// <summary>Revogado explicitamente pelo operador antes de ser resgatado.</summary>
    Revoked,
}

/// <summary>
/// Resgate recusado: token já consumido (replay) ou revogado — fail-closed. Nunca lançada para token
/// expirado (ver <see cref="EnrollmentTokenExpiredException"/>) nem para token inexistente (ver
/// <c>EnrollmentTokenNotFoundException</c> em Contracts, que é o único caso visível para quem apresenta um
/// hash desconhecido).
/// </summary>
public sealed class EnrollmentTokenNotConsumableException(EnrollmentTokenStatus status) : Exception(
    $"Enrollment token não pode ser consumido no estado atual ({status}).")
{
    /// <summary>Estado do token no momento da tentativa.</summary>
    public EnrollmentTokenStatus Status { get; } = status;
}

/// <summary>Resgate recusado: a janela de validade (máximo de 15 minutos) já foi ultrapassada — fail-closed.</summary>
public sealed class EnrollmentTokenExpiredException() : Exception("Enrollment token expirado.");

/// <summary>
/// Enrollment token de uso único (§15.1, AB-4C-001 critério 1): validade máxima de <see cref="MaxTtl"/>,
/// resgatado exatamente uma vez pelo connector que o consome, ou revogado explicitamente pelo operador.
/// O Domain NUNCA vê o segredo bruto — apenas seu <see cref="Sha256Hash"/> (o segredo é gerado e devolvido
/// uma única vez pela Application, nunca persistido). O tenant/projeto do connector resultante é
/// inteiramente determinado por ESTE token, nunca por um valor enviado pelo cliente na chamada de registro
/// — replay, expiração e revogação falham de forma indistinguível de "token inexistente" no limite externo.
/// </summary>
public sealed class EnrollmentToken
{
    /// <summary>Janela máxima permitida entre emissão e expiração (§15.1, AB-4C-001 critério 1).</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(15);

    private const int RequestedByMaxLength = 200;

    private EnrollmentToken(
        EnrollmentTokenId id,
        TenantId tenant,
        ProjectId project,
        Sha256Hash tokenHash,
        string requestedBy,
        CorrelationId correlation,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        EnrollmentTokenStatus status,
        DateTimeOffset? consumedAtUtc,
        ConnectorId? consumedByConnector)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        TokenHash = tokenHash;
        RequestedBy = requestedBy;
        Correlation = correlation;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Status = status;
        ConsumedAtUtc = consumedAtUtc;
        ConsumedByConnector = consumedByConnector;
    }

    /// <summary>
    /// Emite um novo token one-time. A janela (<paramref name="expiresAtUtc"/> - <paramref name="issuedAtUtc"/>)
    /// nunca pode exceder <see cref="MaxTtl"/> — nenhum chamador pode pedir uma janela maior.
    /// </summary>
    /// <exception cref="ArgumentException">Janela inválida (não positiva ou maior que <see cref="MaxTtl"/>).</exception>
    public static EnrollmentToken Issue(
        EnrollmentTokenId id,
        TenantId tenant,
        ProjectId project,
        Sha256Hash tokenHash,
        string requestedBy,
        CorrelationId correlation,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var sanitizedRequestedBy = TextValue.Require(requestedBy, nameof(requestedBy), RequestedByMaxLength);
        if (expiresAtUtc <= issuedAtUtc)
        {
            throw new ArgumentException("expiresAtUtc deve ser posterior a issuedAtUtc.", nameof(expiresAtUtc));
        }

        if (expiresAtUtc - issuedAtUtc > MaxTtl)
        {
            throw new ArgumentException(
                $"Janela do enrollment token excede o máximo permitido ({MaxTtl}).", nameof(expiresAtUtc));
        }

        return new EnrollmentToken(
            id, tenant, project, tokenHash, sanitizedRequestedBy, correlation,
            issuedAtUtc, expiresAtUtc, EnrollmentTokenStatus.Issued, consumedAtUtc: null, consumedByConnector: null);
    }

    /// <summary>Reconstrói um token já persistido (uso exclusivo da camada de persistência).</summary>
    public static EnrollmentToken Rehydrate(
        EnrollmentTokenId id,
        TenantId tenant,
        ProjectId project,
        Sha256Hash tokenHash,
        string requestedBy,
        CorrelationId correlation,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        EnrollmentTokenStatus status,
        DateTimeOffset? consumedAtUtc,
        ConnectorId? consumedByConnector) =>
        new(id, tenant, project, tokenHash, requestedBy, correlation, issuedAtUtc, expiresAtUtc,
            status, consumedAtUtc, consumedByConnector);

    /// <summary>Identidade do token.</summary>
    public EnrollmentTokenId Id { get; }

    /// <summary>Tenant para o qual este token autoriza enrollment (autoridade do connector resultante).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto para o qual este token autoriza enrollment (autoridade do connector resultante).</summary>
    public ProjectId Project { get; }

    /// <summary>Hash SHA-256 do segredo bruto — o segredo em si nunca é persistido.</summary>
    public Sha256Hash TokenHash { get; }

    /// <summary>Identidade autenticada do operador que solicitou a emissão (evidência/auditoria).</summary>
    public string RequestedBy { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante de emissão (UTC).</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>Instante de expiração (UTC) — nunca mais de <see cref="MaxTtl"/> após <see cref="IssuedAtUtc"/>.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Estado atual do token.</summary>
    public EnrollmentTokenStatus Status { get; }

    /// <summary>Instante do resgate — presente apenas quando <see cref="Status"/> é <see cref="EnrollmentTokenStatus.Consumed"/>.</summary>
    public DateTimeOffset? ConsumedAtUtc { get; }

    /// <summary>Connector que resgatou o token — presente apenas quando <see cref="Status"/> é <see cref="EnrollmentTokenStatus.Consumed"/>.</summary>
    public ConnectorId? ConsumedByConnector { get; }

    /// <summary>Verdadeiro quando o token já ultrapassou sua janela de validade no instante informado.</summary>
    public bool IsExpiredAt(DateTimeOffset nowUtc) => nowUtc > ExpiresAtUtc;

    /// <summary>
    /// Resgata o token (uso único) para o connector informado. Fail-closed: token já consumido ou revogado
    /// lança <see cref="EnrollmentTokenNotConsumableException"/>; expirado lança
    /// <see cref="EnrollmentTokenExpiredException"/> mesmo que ainda esteja formalmente <c>Issued</c>.
    /// </summary>
    public EnrollmentToken Consume(ConnectorId connector, DateTimeOffset nowUtc)
    {
        if (Status != EnrollmentTokenStatus.Issued)
        {
            throw new EnrollmentTokenNotConsumableException(Status);
        }

        if (IsExpiredAt(nowUtc))
        {
            throw new EnrollmentTokenExpiredException();
        }

        return new EnrollmentToken(
            Id, Tenant, Project, TokenHash, RequestedBy, Correlation, IssuedAtUtc, ExpiresAtUtc,
            EnrollmentTokenStatus.Consumed, consumedAtUtc: nowUtc, consumedByConnector: connector);
    }

    /// <summary>Revoga o token explicitamente. Um token já consumido não pode ser revogado (o efeito já ocorreu).</summary>
    public EnrollmentToken Revoke()
    {
        if (Status == EnrollmentTokenStatus.Consumed)
        {
            throw new EnrollmentTokenNotConsumableException(Status);
        }

        return new EnrollmentToken(
            Id, Tenant, Project, TokenHash, RequestedBy, Correlation, IssuedAtUtc, ExpiresAtUtc,
            EnrollmentTokenStatus.Revoked, ConsumedAtUtc, ConsumedByConnector);
    }
}
