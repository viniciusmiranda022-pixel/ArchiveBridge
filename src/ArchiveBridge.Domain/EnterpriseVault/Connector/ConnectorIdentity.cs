using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>Estado de registro do connector.</summary>
public enum ConnectorRegistrationStatus
{
    /// <summary>Identidade ativa: pode enviar handshakes e inventário.</summary>
    Active,

    /// <summary>Identidade revogada explicitamente pelo operador — exige novo enrollment para reativar.</summary>
    Revoked,
}

/// <summary>Connector inexistente ou fora do escopo autorizado — indistinguível de "não existe" (anti-IDOR).</summary>
public sealed class ConnectorNotFoundException() : Exception("Connector não encontrado no escopo.");

/// <summary>Operação recusada: o connector já está revogado — fail-closed, exige novo enrollment explícito.</summary>
public sealed class ConnectorRevokedException(ConnectorId connector) : Exception(
    $"Connector {connector.Value:N} está revogado.")
{
    /// <summary>Connector revogado.</summary>
    public ConnectorId Connector { get; } = connector;
}

/// <summary>
/// Identidade durável de um Source Connector registrado (§15.1, AB-4C-001 critério 2): <see cref="Id"/>
/// opaco, thumbprint de chave pública (material PÚBLICO — nunca privado), hostname/site sanitizados,
/// versão e vínculo de tenant/projeto — sempre derivados do <see cref="EnrollmentToken"/> resgatado, nunca
/// de um valor enviado pelo cliente como se fosse autorização. Campos operacionais (hostname/site/versão)
/// podem ser atualizados em reinstalações idempotentes do MESMO connector (mesmo thumbprint no mesmo
/// escopo); a identidade opaca e o vínculo de tenant/projeto nunca mudam depois do registro inicial.
/// </summary>
public sealed class ConnectorIdentity
{
    private const int HostnameMaxLength = 260;
    private const int SiteNameMaxLength = 200;
    private const int VersionMaxLength = 50;

    private ConnectorIdentity(
        ConnectorId id,
        TenantId tenant,
        ProjectId project,
        ConnectorPublicKeyThumbprint thumbprint,
        string hostname,
        string siteName,
        string connectorVersion,
        ConnectorRegistrationStatus status,
        EnrollmentTokenId enrolledViaToken,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Thumbprint = thumbprint;
        Hostname = hostname;
        SiteName = siteName;
        ConnectorVersion = connectorVersion;
        Status = status;
        EnrolledViaToken = enrolledViaToken;
        RegisteredAtUtc = registeredAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Registra uma nova identidade de connector (primeira instalação para este thumbprint/escopo).</summary>
    public static ConnectorIdentity Register(
        ConnectorId id,
        TenantId tenant,
        ProjectId project,
        ConnectorPublicKeyThumbprint thumbprint,
        string hostname,
        string siteName,
        string connectorVersion,
        EnrollmentTokenId enrolledViaToken,
        DateTimeOffset registeredAtUtc) =>
        new(
            id, tenant, project, thumbprint,
            TextValue.Require(hostname, nameof(hostname), HostnameMaxLength),
            TextValue.Require(siteName, nameof(siteName), SiteNameMaxLength),
            TextValue.Require(connectorVersion, nameof(connectorVersion), VersionMaxLength),
            ConnectorRegistrationStatus.Active, enrolledViaToken, registeredAtUtc, registeredAtUtc);

    /// <summary>Reconstrói uma identidade já persistida (uso exclusivo da camada de persistência).</summary>
    public static ConnectorIdentity Rehydrate(
        ConnectorId id,
        TenantId tenant,
        ProjectId project,
        ConnectorPublicKeyThumbprint thumbprint,
        string hostname,
        string siteName,
        string connectorVersion,
        ConnectorRegistrationStatus status,
        EnrollmentTokenId enrolledViaToken,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset updatedAtUtc) =>
        new(id, tenant, project, thumbprint, hostname, siteName, connectorVersion, status,
            enrolledViaToken, registeredAtUtc, updatedAtUtc);

    /// <summary>
    /// Reinstalação idempotente do MESMO connector (mesmo thumbprint/escopo): atualiza apenas campos
    /// operacionais mutáveis e o token de evidência do novo enrollment. Fail-closed se a identidade já foi
    /// revogada — reinstalação NUNCA reativa silenciosamente um connector revogado.
    /// </summary>
    /// <exception cref="ConnectorRevokedException">A identidade atual está revogada.</exception>
    public ConnectorIdentity ReRegister(
        string hostname, string siteName, string connectorVersion, EnrollmentTokenId enrolledViaToken, DateTimeOffset nowUtc)
    {
        if (Status != ConnectorRegistrationStatus.Active)
        {
            throw new ConnectorRevokedException(Id);
        }

        return new ConnectorIdentity(
            Id, Tenant, Project, Thumbprint,
            TextValue.Require(hostname, nameof(hostname), HostnameMaxLength),
            TextValue.Require(siteName, nameof(siteName), SiteNameMaxLength),
            TextValue.Require(connectorVersion, nameof(connectorVersion), VersionMaxLength),
            Status, enrolledViaToken, RegisteredAtUtc, nowUtc);
    }

    /// <summary>Revoga a identidade: bloqueia handshakes/inventário/reinstalação futuros sem novo enrollment.</summary>
    public ConnectorIdentity Revoke(DateTimeOffset nowUtc) =>
        new(Id, Tenant, Project, Thumbprint, Hostname, SiteName, ConnectorVersion,
            ConnectorRegistrationStatus.Revoked, EnrolledViaToken, RegisteredAtUtc, nowUtc);

    /// <summary>Identidade opaca do connector.</summary>
    public ConnectorId Id { get; }

    /// <summary>Tenant do escopo autorizado (derivado do enrollment token, nunca do cliente).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado (derivado do enrollment token, nunca do cliente).</summary>
    public ProjectId Project { get; }

    /// <summary>Thumbprint da chave pública (material público; a chave privada nunca é conhecida pelo Control Plane).</summary>
    public ConnectorPublicKeyThumbprint Thumbprint { get; }

    /// <summary>Hostname sanitizado (evidência/auditoria — nunca usado para roteamento de rede pelo Control Plane).</summary>
    public string Hostname { get; }

    /// <summary>Site Enterprise Vault declarado pelo connector.</summary>
    public string SiteName { get; }

    /// <summary>Versão do software do connector.</summary>
    public string ConnectorVersion { get; }

    /// <summary>Estado de registro atual.</summary>
    public ConnectorRegistrationStatus Status { get; }

    /// <summary>Enrollment token que produziu (ou renovou) este registro — evidência/auditoria.</summary>
    public EnrollmentTokenId EnrolledViaToken { get; }

    /// <summary>Instante do primeiro registro (UTC) — nunca muda em reinstalações.</summary>
    public DateTimeOffset RegisteredAtUtc { get; }

    /// <summary>Instante da última atualização de campos operacionais (UTC).</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Verdadeiro quando o connector pode enviar handshakes/inventário.</summary>
    public bool IsActive => Status == ConnectorRegistrationStatus.Active;
}
