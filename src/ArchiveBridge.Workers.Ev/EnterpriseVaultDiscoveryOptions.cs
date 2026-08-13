namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Configuração tipada e FAIL-CLOSED do Worker EV operacional. O padrão é <c>Enabled=false</c>: sem
/// habilitação explícita o worker operacional não executa. Quando habilitado, as identidades SQL da
/// aplicação e de manutenção, a raiz de evidência e os limites do laço são OBRIGATÓRIOS e validados no
/// startup — configuração inválida derruba o host (nunca degrada silenciosamente). Nenhuma senha/credencial
/// é versionada aqui; os valores concretos vêm da configuração do host on-premises.
/// </summary>
public sealed class EnterpriseVaultDiscoveryOptions
{
    /// <summary>Seção de configuração do Worker EV.</summary>
    public const string SectionName = "EnterpriseVaultDiscovery";

    /// <summary>Teto defensivo do número de escopos por poll (evita enumeração/lotes desproporcionais).</summary>
    public const int MaxScopesPerPollUpperBound = 512;

    /// <summary>Teto defensivo do lote de recuperação de leases por ciclo do reaper.</summary>
    public const int LeaseRecoveryBatchSizeUpperBound = 1000;

    /// <summary>Habilita o worker operacional. Padrão <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Intervalo entre polls, em segundos. Deve ser &gt; 0.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Duração do lease de processamento, em segundos. Deve ser &gt; 0.</summary>
    public int LeaseSeconds { get; set; } = 30;

    /// <summary>Máximo de escopos considerados por poll. Deve estar em [1, <see cref="MaxScopesPerPollUpperBound"/>].</summary>
    public int MaxScopesPerPoll { get; set; } = 32;

    /// <summary>Raiz on-premises do armazenamento imutável de evidência. Obrigatória quando habilitado.</summary>
    public string EvidenceRoot { get; set; } = string.Empty;

    /// <summary>
    /// Executável PowerShell do host Windows e diretório de trabalho controlado da sonda. Usados apenas
    /// quando habilitado em um host Windows; sem credenciais.
    /// </summary>
    public string PowerShellExecutable { get; set; } = "powershell.exe";

    /// <summary>Diretório de trabalho controlado da sonda (vazio ⇒ diretório base do processo).</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Intervalo entre ciclos do reaper de leases expirados do workload EV, em segundos. Deve ser &gt; 0.</summary>
    public int LeaseRecoveryIntervalSeconds { get; set; } = 15;

    /// <summary>Lote máximo de leases recuperados por ciclo do reaper. Deve estar em [1, <see cref="LeaseRecoveryBatchSizeUpperBound"/>].</summary>
    public int LeaseRecoveryBatchSize { get; set; } = 64;

    /// <summary>Intervalo de poll materializado.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>Duração de lease materializada.</summary>
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);

    /// <summary>Intervalo do reaper materializado.</summary>
    public TimeSpan LeaseRecoveryInterval => TimeSpan.FromSeconds(LeaseRecoveryIntervalSeconds);

    /// <summary>
    /// Valida a configuração OPERACIONAL (só faz sentido quando <see cref="Enabled"/>). Fail-closed: qualquer
    /// requisito ausente/ inválido lança <see cref="EnterpriseVaultDiscoveryConfigurationException"/>, que
    /// deve derrubar o startup do host — nunca prosseguir degradado.
    /// </summary>
    public void ValidateForOperation(string? applicationConnectionString, string? maintenanceConnectionString)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(applicationConnectionString))
        {
            errors.Add("ConnectionStrings:Application é obrigatória.");
        }

        if (string.IsNullOrWhiteSpace(maintenanceConnectionString))
        {
            errors.Add("ConnectionStrings:Maintenance é obrigatória.");
        }

        if (string.IsNullOrWhiteSpace(EvidenceRoot))
        {
            errors.Add("EnterpriseVaultDiscovery:EvidenceRoot é obrigatório.");
        }

        if (PollIntervalSeconds <= 0)
        {
            errors.Add("EnterpriseVaultDiscovery:PollIntervalSeconds deve ser > 0.");
        }

        if (LeaseSeconds <= 0)
        {
            errors.Add("EnterpriseVaultDiscovery:LeaseSeconds deve ser > 0.");
        }

        if (MaxScopesPerPoll <= 0 || MaxScopesPerPoll > MaxScopesPerPollUpperBound)
        {
            errors.Add($"EnterpriseVaultDiscovery:MaxScopesPerPoll deve estar em [1, {MaxScopesPerPollUpperBound}].");
        }

        if (LeaseRecoveryIntervalSeconds <= 0)
        {
            errors.Add("EnterpriseVaultDiscovery:LeaseRecoveryIntervalSeconds deve ser > 0.");
        }

        if (LeaseRecoveryBatchSize <= 0 || LeaseRecoveryBatchSize > LeaseRecoveryBatchSizeUpperBound)
        {
            errors.Add($"EnterpriseVaultDiscovery:LeaseRecoveryBatchSize deve estar em [1, {LeaseRecoveryBatchSizeUpperBound}].");
        }

        if (errors.Count > 0)
        {
            throw new EnterpriseVaultDiscoveryConfigurationException(
                "Configuração inválida do Worker EV (fail-closed): " + string.Join(" ", errors));
        }
    }
}

/// <summary>Falha de configuração/startup do Worker EV: fail-closed, o host não deve subir.</summary>
public sealed class EnterpriseVaultDiscoveryConfigurationException(string message) : Exception(message);
