namespace ArchiveBridge.Workers.Pst;

/// <summary>
/// Configuração tipada e FAIL-CLOSED da capacidade de EXECUÇÃO de particionamento (Slice 4B, Passo 3). O
/// padrão é <c>Enabled=false</c>: sem habilitação explícita, nenhum serviço de execução é registrado — o
/// composition root nem valida a raiz de output. Quando habilitado, exige que o PLANEJAMENTO também esteja
/// habilitado (uma execução só materializa um plano já persistido) e uma raiz de output DISTINTA da raiz de
/// custódia de origem — nunca escreve output dentro da árvore read-only de origem.
/// </summary>
public sealed class PstPartitionExecutionOptions
{
    /// <summary>Seção de configuração da capacidade de execução de particionamento.</summary>
    public const string SectionName = "PstPartitionExecution";

    /// <summary>Habilita o registro dos serviços de execução. Padrão <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Raiz NTFS/NAS/SMB de output das partes materializadas. Obrigatória quando habilitado.</summary>
    public string OutputRootPath { get; set; } = string.Empty;

    /// <summary>Margem de segurança adicional (além do tamanho da origem) exigida como espaço livre no preflight, em bytes.</summary>
    public long MinFreeSpaceMarginBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Tempo máximo de uma execução (cópia + verificação) antes de abortar fail-closed, em segundos.</summary>
    public int TimeoutSeconds { get; set; } = 7200;

    /// <summary>Timeout materializado.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>
    /// Valida a configuração OPERACIONAL. Fail-closed: execução exige planejamento habilitado (só executa
    /// um plano já persistido) e raiz de output válida e distinta da raiz de custódia de origem.
    /// </summary>
    /// <exception cref="PstInspectionConfigurationException">Configuração inválida — o host não deve subir.</exception>
    public void ValidateForOperation(bool planningEnabled, string sourceRootPath)
    {
        var errors = new List<string>();
        if (!planningEnabled)
        {
            errors.Add($"{SectionName}:Enabled exige {PstPartitionPlanningOptions.SectionName}:Enabled=true (só executa um plano já persistido).");
        }

        if (string.IsNullOrWhiteSpace(OutputRootPath))
        {
            errors.Add($"{SectionName}:OutputRootPath é obrigatório.");
        }
        else if (!string.IsNullOrWhiteSpace(sourceRootPath)
            && string.Equals(Path.GetFullPath(OutputRootPath), Path.GetFullPath(sourceRootPath), StringComparison.Ordinal))
        {
            errors.Add($"{SectionName}:OutputRootPath deve ser distinto da raiz de custódia de origem ({PstInspectionOptions.SectionName}:RootPath).");
        }

        if (MinFreeSpaceMarginBytes < 0)
        {
            errors.Add($"{SectionName}:MinFreeSpaceMarginBytes não pode ser negativo.");
        }

        if (TimeoutSeconds <= 0)
        {
            errors.Add($"{SectionName}:TimeoutSeconds deve ser > 0.");
        }

        if (errors.Count > 0)
        {
            throw new PstInspectionConfigurationException(
                "Configuração inválida da capacidade de execução de particionamento (fail-closed): " + string.Join(" ", errors));
        }
    }
}
