namespace ArchiveBridge.Workers.Pst;

/// <summary>
/// Configuração tipada e FAIL-CLOSED da capacidade de inspeção PST (Slice 4B, Passo 1). O padrão é
/// <c>Enabled=false</c>: sem habilitação explícita, nenhum serviço de inspeção é registrado no
/// composition root — <see cref="Composition.PstInspectionComposition"/> nem tenta abrir conexão. Quando
/// habilitado, a raiz de custódia e as identidades SQL são OBRIGATÓRIAS e validadas no startup;
/// configuração inválida derruba o host (nunca degrada silenciosamente). Nenhuma credencial é versionada
/// aqui — os valores concretos vêm da configuração do host on-premises.
/// </summary>
public sealed class PstInspectionOptions
{
    /// <summary>Seção de configuração da capacidade de inspeção PST.</summary>
    public const string SectionName = "PstInspection";

    /// <summary>Habilita o registro dos serviços de inspeção. Padrão <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Raiz NTFS/NAS/SMB de custódia de PSTs sob a qual todo caminho relativo é resolvido/contido. Obrigatória quando habilitado.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Tamanho máximo aceito para inspeção, em bytes. Deve ser &gt; 0.</summary>
    public long MaxSizeBytes { get; set; } = 200L * 1024 * 1024 * 1024;

    /// <summary>Tempo máximo de uma inspeção antes de abortar fail-closed, em segundos. Deve ser &gt; 0.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Timeout materializado.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>
    /// Valida a configuração OPERACIONAL (só faz sentido quando <see cref="Enabled"/>). Fail-closed:
    /// qualquer requisito ausente/inválido lança <see cref="PstInspectionConfigurationException"/>, que
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

        if (string.IsNullOrWhiteSpace(RootPath))
        {
            errors.Add("PstInspection:RootPath é obrigatório.");
        }

        if (MaxSizeBytes <= 0)
        {
            errors.Add("PstInspection:MaxSizeBytes deve ser > 0.");
        }

        if (TimeoutSeconds <= 0)
        {
            errors.Add("PstInspection:TimeoutSeconds deve ser > 0.");
        }

        if (errors.Count > 0)
        {
            throw new PstInspectionConfigurationException(
                "Configuração inválida da capacidade de inspeção PST (fail-closed): " + string.Join(" ", errors));
        }
    }
}

/// <summary>Falha de configuração/startup da capacidade de inspeção PST: fail-closed, o host não deve subir.</summary>
public sealed class PstInspectionConfigurationException(string message) : Exception(message);
