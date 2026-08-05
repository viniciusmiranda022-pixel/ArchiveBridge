namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Validação FAIL-CLOSED do comando de descoberta: contexto obrigatório presente e schema conhecido.
/// Aplicada na factory, no enfileiramento e no processamento (defesa em profundidade). Sem PII. Vive em
/// Contracts para que a fila durável (Infraestrutura) a aplique ANTES de enfileirar.
/// </summary>
public static class EvDiscoveryCommandValidation
{
    /// <summary>Garante que o comando é bem-formado; lança <see cref="ArgumentException"/> se não for.</summary>
    public static void EnsureValid(EvDiscoveryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Context.SchemaVersion != EvDiscoveryCommandContext.CurrentSchemaVersion)
        {
            throw new ArgumentException(
                $"Schema de comando de descoberta desconhecido: {command.Context.SchemaVersion}.", nameof(command));
        }

        if (command.EnvironmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Comando de descoberta exige um EnvironmentId válido.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.SiteName) || string.IsNullOrWhiteSpace(command.DirectoryServer))
        {
            throw new ArgumentException("Comando de descoberta exige SiteName e DirectoryServer.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.RequestedBy))
        {
            throw new ArgumentException("Comando de descoberta exige RequestedBy.", nameof(command));
        }

        if (command.Context.DiscoveryPolicyVersion < 1)
        {
            throw new ArgumentException("Comando de descoberta exige DiscoveryPolicyVersion válido.", nameof(command));
        }

        if (command.Context.ExpectedProjectConfigurationVersion is null || command.Context.ExpectedConfigurationHash is null)
        {
            throw new ArgumentException(
                "Comando de descoberta exige a versão e o hash de configuração esperados do projeto.", nameof(command));
        }
    }
}
