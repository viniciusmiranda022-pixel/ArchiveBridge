using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Papel mínimo exigido para autorizar um freeze — fail-closed, NUNCA inferido do request;
/// <see cref="Unspecified"/> é sempre recusado por <see cref="EvFreezePlan.AuthorizeFreeze"/>.
/// </summary>
public enum EvFreezeAuthorizationRole
{
    /// <summary>Role ausente/não informado — nunca autoriza (fail-closed).</summary>
    Unspecified,

    /// <summary>Operador de migração com permissão operacional sobre o projeto.</summary>
    MigrationOperator,

    /// <summary>Administrador do tenant — role de maior privilégio explicitamente elegível.</summary>
    TenantAdministrator,
}

/// <summary>Autorização FORMAL de um freeze — persistida, correlacionada, nunca implícita ou inferida de CI verde/estado técnico.</summary>
public sealed record EvFreezeAuthorization(
    string AuthorizedBy,
    EvFreezeAuthorizationRole Role,
    string Justification,
    CorrelationId Correlation,
    DateTimeOffset AuthorizedAtUtc);

/// <summary>
/// Plano de freeze/cutover de UM archive (AB-4C-008 req 9-11): agregado append-oriented que representa
/// SOMENTE estado e autorização — NENHUM método desta classe executa (ou aciona) uma ação real no
/// Enterprise Vault; toda transição é fail-closed via <see cref="EvFreezeTransitions"/>.
/// </summary>
public sealed class EvFreezePlan
{
    private const int JustificationMaxLength = 2000;
    private const int AuthorizedByMaxLength = 200;

    private EvFreezePlan(
        FreezePlanId id,
        TenantId tenant,
        ProjectId project,
        ConnectorId connector,
        string externalArchiveId,
        EvFreezeStatus status,
        EvFreezeAuthorization? authorization,
        int version)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Connector = connector;
        ExternalArchiveId = externalArchiveId;
        Status = status;
        Authorization = authorization;
        Version = version;
    }

    /// <summary>Cria um novo plano de freeze no estado inicial <see cref="EvFreezeStatus.FreezeRequired"/>.</summary>
    public static EvFreezePlan RequestFreeze(TenantId tenant, ProjectId project, ConnectorId connector, string externalArchiveId)
    {
        var archiveId = TextValue.Require(externalArchiveId, nameof(externalArchiveId), 300);
        return new EvFreezePlan(FreezePlanId.New(), tenant, project, connector, archiveId, EvFreezeStatus.FreezeRequired, authorization: null, version: 1);
    }

    /// <summary>Reconstrói um plano já persistido (uso exclusivo da camada de persistência).</summary>
    public static EvFreezePlan Rehydrate(
        FreezePlanId id,
        TenantId tenant,
        ProjectId project,
        ConnectorId connector,
        string externalArchiveId,
        EvFreezeStatus status,
        EvFreezeAuthorization? authorization,
        int version) =>
        new(id, tenant, project, connector, externalArchiveId, status, authorization, version);

    /// <summary>Identidade opaca do plano.</summary>
    public FreezePlanId Id { get; }

    /// <summary>Tenant do escopo.</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo.</summary>
    public ProjectId Project { get; }

    /// <summary>Connector do archive-alvo.</summary>
    public ConnectorId Connector { get; }

    /// <summary>Archive externo opaco a que este plano pertence.</summary>
    public string ExternalArchiveId { get; }

    /// <summary>Estado corrente do plano.</summary>
    public EvFreezeStatus Status { get; private set; }

    /// <summary>Autorização vigente (não nula somente a partir de <see cref="EvFreezeStatus.FreezeAuthorized"/>).</summary>
    public EvFreezeAuthorization? Authorization { get; private set; }

    /// <summary>Versão otimista do plano — incrementada a cada transição, para concorrência segura na persistência.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Autoriza formalmente o freeze (passo 31) — exige role competente e justificativa; NUNCA aciona
    /// nenhuma ação real no EV. Fail-closed: <see cref="EvFreezeAuthorizationRole.Unspecified"/> é sempre recusado.
    /// </summary>
    /// <exception cref="EvFreezeAuthorizationRequiredException">Role não informado/inválido.</exception>
    public void AuthorizeFreeze(string authorizedBy, EvFreezeAuthorizationRole role, string justification, CorrelationId correlation, DateTimeOffset atUtc)
    {
        EvFreezeTransitions.EnsureCanTransition(Status, EvFreezeStatus.FreezeAuthorized);
        if (role == EvFreezeAuthorizationRole.Unspecified)
        {
            throw new EvFreezeAuthorizationRequiredException("Autorização de freeze exige role competente explícito (fail-closed).");
        }

        var sanitizedBy = TextValue.Require(authorizedBy, nameof(authorizedBy), AuthorizedByMaxLength);
        var sanitizedJustification = TextValue.Require(justification, nameof(justification), JustificationMaxLength);

        Authorization = new EvFreezeAuthorization(sanitizedBy, role, sanitizedJustification, correlation, atUtc);
        Status = EvFreezeStatus.FreezeAuthorized;
        Version++;
    }

    /// <summary>Recusa o freeze solicitado — nenhuma autorização é persistida.</summary>
    public void RejectFreeze()
    {
        EvFreezeTransitions.EnsureCanTransition(Status, EvFreezeStatus.FreezeRejected);
        Authorization = null;
        Status = EvFreezeStatus.FreezeRejected;
        Version++;
    }

    /// <summary>Re-solicita o freeze após uma recusa anterior.</summary>
    public void ReRequestFreeze()
    {
        EvFreezeTransitions.EnsureCanTransition(Status, EvFreezeStatus.FreezeRequired);
        Status = EvFreezeStatus.FreezeRequired;
        Version++;
    }

    /// <summary>Marca o delta final como concluído sob freeze — exige autorização JÁ persistida (precondição fail-closed).</summary>
    /// <exception cref="EvFreezeAuthorizationRequiredException">Nenhuma autorização persistida.</exception>
    public void MarkFinalDeltaReady()
    {
        EvFreezeTransitions.EnsureCanTransition(Status, EvFreezeStatus.FinalDeltaReady);
        if (Authorization is null)
        {
            throw new EvFreezeAuthorizationRequiredException("FinalDeltaReady exige autorização de freeze persistida (fail-closed).");
        }

        Status = EvFreezeStatus.FinalDeltaReady;
        Version++;
    }

    /// <summary>Marca o cutover como concluído — o EV entra em janela de retenção de rollback contratual (passo 34).</summary>
    public void MarkRollbackRetentionRequired()
    {
        EvFreezeTransitions.EnsureCanTransition(Status, EvFreezeStatus.RollbackRetentionRequired);
        Status = EvFreezeStatus.RollbackRetentionRequired;
        Version++;
    }

    /// <summary>
    /// Transição SEMPRE terminal neste Passo (STOP-THE-LINE, req 11): descomissionamento exige sign-off,
    /// reconciliação e retenção satisfeitos por gates de um Passo POSTERIOR — nunca por esta classe.
    /// Chamar este método nunca libera descomissionamento; apenas registra o bloqueio explícito.
    /// </summary>
    public void BlockDecommission()
    {
        EvFreezeTransitions.EnsureCanTransition(Status, EvFreezeStatus.DecommissionBlocked);
        Status = EvFreezeStatus.DecommissionBlocked;
        Version++;
    }
}
