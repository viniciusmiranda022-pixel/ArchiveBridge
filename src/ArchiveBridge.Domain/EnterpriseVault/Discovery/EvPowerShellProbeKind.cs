namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Operação TIPADA de sondagem read-only do Enterprise Vault. O host mapeia cada valor para um script
/// interno FIXO, imutável e versionado (nunca script do usuário, nunca CommandName arbitrário). Nenhuma
/// operação executa <c>Export-EVArchive</c> — no máximo descobre seus METADADOS via <c>Get-Command</c>.
/// </summary>
public enum EvPowerShellProbeKind
{
    /// <summary>Ambiente PowerShell disponível (versão do host).</summary>
    PowerShellEnvironment,

    /// <summary>Snap-in do Enterprise Vault registrado.</summary>
    RegisteredEvSnapin,

    /// <summary>Módulo do Enterprise Vault disponível.</summary>
    AvailableEvModule,

    /// <summary>Descoberta de sites (<c>Get-EVSite</c>).</summary>
    EvSite,

    /// <summary>Descoberta de servidores (<c>Get-EVServer</c>).</summary>
    EvServer,

    /// <summary>Descoberta de vault stores (<c>Get-EVVaultStore</c>).</summary>
    EvVaultStore,

    /// <summary>Descoberta de archives (<c>Get-EVArchive</c>).</summary>
    EvArchive,

    /// <summary>Metadados do comando de exportação (<c>Get-Command Export-EVArchive</c>) — NÃO executa a exportação.</summary>
    ExportEvArchiveCommandMetadata,

    /// <summary>Acesso ao caminho de staging (<c>Test-Path</c>).</summary>
    StagingPathAccess,
}

/// <summary>Categoria de erro de uma sonda/capacidade (para classificação estruturada, não por mensagem).</summary>
public enum EvErrorCategory
{
    /// <summary>Sem erro.</summary>
    None,

    /// <summary>Capacidade comprovadamente ausente.</summary>
    NotAvailable,

    /// <summary>Permissão insuficiente para a sonda.</summary>
    PermissionDenied,

    /// <summary>A sonda excedeu o timeout.</summary>
    Timeout,

    /// <summary>Saída inválida/malformada (envelope/JSON/schema/tipo).</summary>
    OutputInvalid,

    /// <summary>Falha de execução (exit code, stderr, limite de saída).</summary>
    ExecutionFailed,
}
