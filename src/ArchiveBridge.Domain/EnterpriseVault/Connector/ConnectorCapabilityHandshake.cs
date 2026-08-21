using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>Identidade de um handshake de capability/versão, gerada pelo servidor.</summary>
public readonly record struct CapabilityHandshakeId(Guid Value)
{
    /// <summary>Gera uma nova identidade de handshake.</summary>
    public static CapabilityHandshakeId New() => new(Guid.NewGuid());
}

/// <summary>
/// Resultado de UM handshake de capability/versão entre o connector e o host EV (AB-4C-001 critérios 4/5).
/// <see cref="ExportCapable"/> é SEMPRE derivada aqui a partir de <see cref="ConnectorSupportMatrix"/> —
/// nunca setada diretamente por Infrastructure/Application — e só é verdadeira quando o nível de suporte é
/// <see cref="ConnectorSupportLevel.Certified"/> E o snap-in PowerShell está disponível; qualquer schema
/// desconhecido/incompatível bloqueia a capacidade com um diagnóstico estruturado
/// (<see cref="BlockingReason"/>), nunca com improviso. Cada handshake é um registro append-only; o mais
/// recente por <see cref="CollectedAtUtc"/> é o vigente.
/// </summary>
public sealed record ConnectorCapabilityHandshake
{
    private const int EvVersionDisplayMaxLength = 100;

    private ConnectorCapabilityHandshake(
        CapabilityHandshakeId id,
        ConnectorId connector,
        TenantId tenant,
        ProjectId project,
        string evVersionDisplay,
        bool powerShellSnapinAvailable,
        ConnectorSupportLevel supportLevel,
        bool exportCapable,
        string? blockingReason,
        CorrelationId correlation,
        DateTimeOffset collectedAtUtc)
    {
        Id = id;
        Connector = connector;
        Tenant = tenant;
        Project = project;
        EvVersionDisplay = evVersionDisplay;
        PowerShellSnapinAvailable = powerShellSnapinAvailable;
        SupportLevel = supportLevel;
        ExportCapable = exportCapable;
        BlockingReason = blockingReason;
        Correlation = correlation;
        CollectedAtUtc = collectedAtUtc;
    }

    /// <summary>Avalia um novo handshake a partir da observação bruta do connector.</summary>
    public static ConnectorCapabilityHandshake Evaluate(
        CapabilityHandshakeId id,
        ConnectorId connector,
        TenantId tenant,
        ProjectId project,
        string evVersionDisplay,
        bool powerShellSnapinAvailable,
        CorrelationId correlation,
        DateTimeOffset collectedAtUtc)
    {
        var sanitizedVersion = TextValue.Require(evVersionDisplay, nameof(evVersionDisplay), EvVersionDisplayMaxLength);
        var supportLevel = ConnectorSupportMatrix.Evaluate(sanitizedVersion);
        var exportCapable = supportLevel == ConnectorSupportLevel.Certified && powerShellSnapinAvailable;
        var blockingReason = exportCapable ? null : DescribeBlockingReason(supportLevel, powerShellSnapinAvailable);

        return new ConnectorCapabilityHandshake(
            id, connector, tenant, project, sanitizedVersion, powerShellSnapinAvailable,
            supportLevel, exportCapable, blockingReason, correlation, collectedAtUtc);
    }

    /// <summary>Reconstrói um handshake já persistido (uso exclusivo da camada de persistência).</summary>
    public static ConnectorCapabilityHandshake Rehydrate(
        CapabilityHandshakeId id,
        ConnectorId connector,
        TenantId tenant,
        ProjectId project,
        string evVersionDisplay,
        bool powerShellSnapinAvailable,
        ConnectorSupportLevel supportLevel,
        bool exportCapable,
        string? blockingReason,
        CorrelationId correlation,
        DateTimeOffset collectedAtUtc) =>
        new(id, connector, tenant, project, evVersionDisplay, powerShellSnapinAvailable,
            supportLevel, exportCapable, blockingReason, correlation, collectedAtUtc);

    // Diagnóstico estruturado — nunca uma mensagem livre interpolando dado do ambiente (sem PII/segredo).
    private static string DescribeBlockingReason(ConnectorSupportLevel level, bool snapinAvailable) => level switch
    {
        ConnectorSupportLevel.Unknown => "SCHEMA_VERSION_UNKNOWN",
        ConnectorSupportLevel.NotSupported => "FAMILY_NOT_SUPPORTED",
        _ when !snapinAvailable => "POWERSHELL_SNAPIN_UNAVAILABLE",
        _ => "FAMILY_NOT_CERTIFIED",
    };

    /// <summary>Identidade do handshake.</summary>
    public CapabilityHandshakeId Id { get; }

    /// <summary>Connector que reportou o handshake.</summary>
    public ConnectorId Connector { get; }

    /// <summary>Tenant do escopo (herdado da identidade do connector).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo (herdado da identidade do connector).</summary>
    public ProjectId Project { get; }

    /// <summary>Versão do Enterprise Vault observada (string de exibição — a decisão nunca usa só isto).</summary>
    public string EvVersionDisplay { get; }

    /// <summary>Verdadeiro quando o snap-in PowerShell do EV foi observado disponível no host do connector.</summary>
    public bool PowerShellSnapinAvailable { get; }

    /// <summary>Nível de suporte resolvido pela matriz de compatibilidade.</summary>
    public ConnectorSupportLevel SupportLevel { get; }

    /// <summary>Verdadeiro somente quando <see cref="SupportLevel"/> é Certified E o snap-in está disponível.</summary>
    public bool ExportCapable { get; }

    /// <summary>Código estruturado do bloqueio — presente somente quando <see cref="ExportCapable"/> é falso.</summary>
    public string? BlockingReason { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante da coleta (UTC).</summary>
    public DateTimeOffset CollectedAtUtc { get; }
}
