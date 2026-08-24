namespace ArchiveBridge.Workers.Upload;

/// <summary>
/// Configuração tipada e FAIL-CLOSED do Worker de upload Purview operacional (AB-I5-009). Padrão
/// <c>Enabled=false</c>: sem habilitação explícita nenhum efeito de upload é registrado. Quando
/// habilitado, o binário AzCopy homologado e as raízes locais autorizadas são OBRIGATÓRIOS e validados no
/// startup — configuração inválida derruba o host. Nenhuma credencial é versionada aqui.
/// </summary>
public sealed class PurviewUploadWorkerOptions
{
    /// <summary>Seção de configuração do Worker de upload Purview.</summary>
    public const string SectionName = "PurviewUpload";

    /// <summary>Teto defensivo do número de escopos por poll.</summary>
    public const int MaxScopesPerPollUpperBound = 512;

    /// <summary>Teto defensivo do lote de recuperação de leases por ciclo do reaper.</summary>
    public const int LeaseRecoveryBatchSizeUpperBound = 1000;

    /// <summary>Habilita o worker operacional de upload. Padrão <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Intervalo entre polls, em segundos. Deve ser &gt; 0.</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>Duração do lease de processamento, em segundos. Deve ser &gt; 0 (execuções longas exigem lease renovado por heartbeat).</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Máximo de escopos considerados por poll. Deve estar em [1, <see cref="MaxScopesPerPollUpperBound"/>].</summary>
    public int MaxScopesPerPoll { get; set; } = 16;

    /// <summary>Raiz local server-side sob a qual os outputs de particionamento (Slice 4B) são lidos para revalidação/transporte. Obrigatória quando habilitado.</summary>
    public string PartitionOutputRoot { get; set; } = string.Empty;

    /// <summary>Caminho absoluto do binário AzCopy homologado. Obrigatório quando habilitado.</summary>
    public string AzCopyExecutablePath { get; set; } = string.Empty;

    /// <summary>Versão declarada do binário AzCopy homologado. Obrigatória quando habilitado.</summary>
    public string AzCopyDeclaredVersion { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) homologados para <see cref="AzCopyDeclaredVersion"/>. Ao menos um obrigatório quando habilitado.</summary>
    public IReadOnlyList<string> AzCopyHomologatedSha256Hexes { get; set; } = [];

    /// <summary>Raiz local server-side dedicada de logs/plan do AzCopy por tentativa. Obrigatória quando habilitado.</summary>
    public string AzCopyLogRoot { get; set; } = string.Empty;

    /// <summary>Timeout máximo de UM upload de arquivo, em minutos. Deve ser &gt; 0.</summary>
    public int AzCopyProcessTimeoutMinutes { get; set; } = 240;

    /// <summary>Duração do lease de claim do SAS (Passo 2), em minutos. Deve ser &gt; 0.</summary>
    public int SasClaimLeaseMinutes { get; set; } = 5;

    /// <summary>Intervalo entre ciclos do reaper de leases expirados do workload Upload, em segundos. Deve ser &gt; 0.</summary>
    public int LeaseRecoveryIntervalSeconds { get; set; } = 30;

    /// <summary>Lote máximo de leases recuperados por ciclo do reaper. Deve estar em [1, <see cref="LeaseRecoveryBatchSizeUpperBound"/>].</summary>
    public int LeaseRecoveryBatchSize { get; set; } = 32;

    /// <summary>Intervalo de poll materializado.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>Duração de lease materializada.</summary>
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);

    /// <summary>Timeout de processo AzCopy materializado.</summary>
    public TimeSpan AzCopyProcessTimeout => TimeSpan.FromMinutes(AzCopyProcessTimeoutMinutes);

    /// <summary>Duração do lease de claim do SAS materializada.</summary>
    public TimeSpan SasClaimLeaseDuration => TimeSpan.FromMinutes(SasClaimLeaseMinutes);

    /// <summary>Intervalo do reaper materializado.</summary>
    public TimeSpan LeaseRecoveryInterval => TimeSpan.FromSeconds(LeaseRecoveryIntervalSeconds);

    /// <summary>
    /// Valida a configuração OPERACIONAL. Fail-closed: qualquer requisito ausente/inválido lança
    /// <see cref="PurviewUploadConfigurationException"/>, que deve derrubar o startup do host.
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

        if (string.IsNullOrWhiteSpace(PartitionOutputRoot))
        {
            errors.Add("PurviewUpload:PartitionOutputRoot é obrigatório.");
        }

        if (string.IsNullOrWhiteSpace(AzCopyExecutablePath))
        {
            errors.Add("PurviewUpload:AzCopyExecutablePath é obrigatório.");
        }

        if (string.IsNullOrWhiteSpace(AzCopyDeclaredVersion))
        {
            errors.Add("PurviewUpload:AzCopyDeclaredVersion é obrigatório.");
        }

        if (AzCopyHomologatedSha256Hexes.Count == 0)
        {
            errors.Add("PurviewUpload:AzCopyHomologatedSha256Hexes deve conter ao menos um SHA-256 homologado.");
        }

        if (string.IsNullOrWhiteSpace(AzCopyLogRoot))
        {
            errors.Add("PurviewUpload:AzCopyLogRoot é obrigatório.");
        }

        if (PollIntervalSeconds <= 0)
        {
            errors.Add("PurviewUpload:PollIntervalSeconds deve ser > 0.");
        }

        if (LeaseSeconds <= 0)
        {
            errors.Add("PurviewUpload:LeaseSeconds deve ser > 0.");
        }

        if (MaxScopesPerPoll <= 0 || MaxScopesPerPoll > MaxScopesPerPollUpperBound)
        {
            errors.Add($"PurviewUpload:MaxScopesPerPoll deve estar em [1, {MaxScopesPerPollUpperBound}].");
        }

        if (AzCopyProcessTimeoutMinutes <= 0)
        {
            errors.Add("PurviewUpload:AzCopyProcessTimeoutMinutes deve ser > 0.");
        }

        if (SasClaimLeaseMinutes <= 0)
        {
            errors.Add("PurviewUpload:SasClaimLeaseMinutes deve ser > 0.");
        }

        if (LeaseRecoveryIntervalSeconds <= 0)
        {
            errors.Add("PurviewUpload:LeaseRecoveryIntervalSeconds deve ser > 0.");
        }

        if (LeaseRecoveryBatchSize <= 0 || LeaseRecoveryBatchSize > LeaseRecoveryBatchSizeUpperBound)
        {
            errors.Add($"PurviewUpload:LeaseRecoveryBatchSize deve estar em [1, {LeaseRecoveryBatchSizeUpperBound}].");
        }

        if (errors.Count > 0)
        {
            throw new PurviewUploadConfigurationException(
                "Configuração inválida do Worker de upload Purview (fail-closed): " + string.Join(" ", errors));
        }
    }
}

/// <summary>Falha de configuração/startup do Worker de upload Purview: fail-closed, o host não deve subir.</summary>
public sealed class PurviewUploadConfigurationException(string message) : Exception(message);
