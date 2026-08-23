namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Configuração tipada e FAIL-CLOSED do Worker de exportação EV operacional (AB-4C-005, Slice 4C Passo 2).
/// Padrão <c>Enabled=false</c>: sem habilitação explícita nenhum efeito de exportação é registrado. Quando
/// habilitado, a raiz local autorizada de output é OBRIGATÓRIA e validada no startup — configuração
/// inválida derruba o host. Nenhuma credencial é versionada aqui.
/// </summary>
public sealed class EnterpriseVaultExportOptions
{
    /// <summary>Seção de configuração do Worker de exportação EV.</summary>
    public const string SectionName = "EnterpriseVaultExport";

    /// <summary>Teto defensivo do número de escopos por poll.</summary>
    public const int MaxScopesPerPollUpperBound = 512;

    /// <summary>Teto defensivo do lote de recuperação de leases por ciclo do reaper.</summary>
    public const int LeaseRecoveryBatchSizeUpperBound = 1000;

    /// <summary>Habilita o worker operacional de exportação. Padrão <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Intervalo entre polls, em segundos. Deve ser &gt; 0.</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>Duração do lease de processamento, em segundos. Deve ser &gt; 0 (execuções longas exigem lease renovado por heartbeat).</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Máximo de escopos considerados por poll. Deve estar em [1, <see cref="MaxScopesPerPollUpperBound"/>].</summary>
    public int MaxScopesPerPoll { get; set; } = 16;

    /// <summary>Raiz local autorizada de output de exportação (server-side, nunca fornecida pelo cliente). Obrigatória quando habilitado.</summary>
    public string OutputRoot { get; set; } = string.Empty;

    /// <summary>Raízes UNC allowlisted, caso <see cref="OutputRoot"/> seja um caminho UNC (§15.2).</summary>
    public IReadOnlyList<string> AllowedUncRoots { get; set; } = [];

    /// <summary>Executável PowerShell do host Windows e diretório de trabalho controlado. Usados apenas quando habilitado em host Windows.</summary>
    public string PowerShellExecutable { get; set; } = "powershell.exe";

    /// <summary>Diretório de trabalho controlado (vazio ⇒ diretório base do processo).</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Timeout máximo de execução de UMA tentativa, em minutos. Deve ser &gt; 0.</summary>
    public int ProcessTimeoutMinutes { get; set; } = 720;

    /// <summary>Intervalo entre ciclos do reaper de leases expirados do workload EV, em segundos. Deve ser &gt; 0.</summary>
    public int LeaseRecoveryIntervalSeconds { get; set; } = 30;

    /// <summary>Lote máximo de leases recuperados por ciclo do reaper. Deve estar em [1, <see cref="LeaseRecoveryBatchSizeUpperBound"/>].</summary>
    public int LeaseRecoveryBatchSize { get; set; } = 32;

    /// <summary>Intervalo de poll materializado.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>Duração de lease materializada.</summary>
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);

    /// <summary>Timeout de processo materializado.</summary>
    public TimeSpan ProcessTimeout => TimeSpan.FromMinutes(ProcessTimeoutMinutes);

    /// <summary>Intervalo do reaper materializado.</summary>
    public TimeSpan LeaseRecoveryInterval => TimeSpan.FromSeconds(LeaseRecoveryIntervalSeconds);

    /// <summary>
    /// Valida a configuração OPERACIONAL. Fail-closed: qualquer requisito ausente/inválido lança
    /// <see cref="EnterpriseVaultExportConfigurationException"/>, que deve derrubar o startup do host.
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

        if (string.IsNullOrWhiteSpace(OutputRoot))
        {
            errors.Add("EnterpriseVaultExport:OutputRoot é obrigatório.");
        }

        if (PollIntervalSeconds <= 0)
        {
            errors.Add("EnterpriseVaultExport:PollIntervalSeconds deve ser > 0.");
        }

        if (LeaseSeconds <= 0)
        {
            errors.Add("EnterpriseVaultExport:LeaseSeconds deve ser > 0.");
        }

        if (MaxScopesPerPoll <= 0 || MaxScopesPerPoll > MaxScopesPerPollUpperBound)
        {
            errors.Add($"EnterpriseVaultExport:MaxScopesPerPoll deve estar em [1, {MaxScopesPerPollUpperBound}].");
        }

        if (ProcessTimeoutMinutes <= 0)
        {
            errors.Add("EnterpriseVaultExport:ProcessTimeoutMinutes deve ser > 0.");
        }

        if (LeaseRecoveryIntervalSeconds <= 0)
        {
            errors.Add("EnterpriseVaultExport:LeaseRecoveryIntervalSeconds deve ser > 0.");
        }

        if (LeaseRecoveryBatchSize <= 0 || LeaseRecoveryBatchSize > LeaseRecoveryBatchSizeUpperBound)
        {
            errors.Add($"EnterpriseVaultExport:LeaseRecoveryBatchSize deve estar em [1, {LeaseRecoveryBatchSizeUpperBound}].");
        }

        if (errors.Count > 0)
        {
            throw new EnterpriseVaultExportConfigurationException(
                "Configuração inválida do Worker de exportação EV (fail-closed): " + string.Join(" ", errors));
        }
    }
}

/// <summary>Falha de configuração/startup do Worker de exportação EV: fail-closed, o host não deve subir.</summary>
public sealed class EnterpriseVaultExportConfigurationException(string message) : Exception(message);
