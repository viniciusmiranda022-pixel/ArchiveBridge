namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>Identidade opaca de um Source Connector, gerada pelo servidor no registro (§15.1).</summary>
public readonly record struct ConnectorId(Guid Value)
{
    /// <summary>Gera uma nova identidade opaca de connector.</summary>
    public static ConnectorId New() => new(Guid.NewGuid());
}
