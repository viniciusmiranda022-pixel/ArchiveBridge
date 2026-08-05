namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>Nomes de parâmetro documentados do <c>Export-EVArchive</c> (Enterprise Vault 15.1).</summary>
public static class EvExportParameters
{
    /// <summary>Identificador do archive a exportar.</summary>
    public const string ArchiveId = "ArchiveId";

    /// <summary>Diretório de saída da exportação (documentado; substitui o antigo/ficcional OutputPath).</summary>
    public const string OutputDirectory = "OutputDirectory";

    /// <summary>Filtro de busca da exportação (filtering).</summary>
    public const string SearchString = "SearchString";

    /// <summary>Formato de saída (com suporte a PST).</summary>
    public const string Format = "Format";

    /// <summary>Número máximo de threads.</summary>
    public const string MaxThreads = "MaxThreads";

    /// <summary>Política de retry do cmdlet.</summary>
    public const string Retry = "Retry";

    /// <summary>Tamanho máximo de PST (MB).</summary>
    public const string MaxPstSizeMb = "MaxPSTSizeMB";
}

/// <summary>
/// Maturidade de um perfil de assinatura, registrada SEPARADAMENTE e sem confundir níveis: se a forma foi
/// observada em runtime (<see cref="RuntimeObserved"/>), se corresponde à documentação oficial
/// (<see cref="OfficialDocumentation"/>), se foi validada por FIXTURES automatizados/mocks controlados
/// (<see cref="AutomatedFixtureValidated"/>) e se foi homologada em LABORATÓRIO com o produto Veritas real
/// (<see cref="LaboratoryValidated"/>). <b>Nunca</b> se declara <see cref="LaboratoryValidated"/> sem prova
/// de laboratório; a validação por fixtures NÃO é laboratório.
/// </summary>
public sealed record EvExportMaturity(
    bool RuntimeObserved, bool OfficialDocumentation, bool AutomatedFixtureValidated, bool LaboratoryValidated);

/// <summary>
/// Perfil DOCUMENTADO da assinatura do <c>Export-EVArchive</c>. Descreve os parâmetros identificadores e
/// documentados da versão, com a maturidade explícita. O perfil oficial 15.1 é
/// <see cref="Documented151"/>: <see cref="EvExportMaturity.OfficialDocumentation"/> = true (da
/// documentação), <see cref="EvExportMaturity.LaboratoryValidated"/> = false (não homologado). O relatório
/// de exportação é gerado AUTOMATICAMENTE (<c>ExportReport_&lt;datetime&gt;.txt</c>) — não há parâmetro
/// <c>GenerateReport</c>, portanto o suporte a relatório não depende de parâmetro.
/// </summary>
public sealed record EvExportProfile(
    string ProfileId,
    IReadOnlyList<string> IdentifyingParameters,
    IReadOnlyList<string> DocumentedParameters,
    EvExportMaturity DocumentedMaturity)
{
    /// <summary>Identificador do perfil oficial documentado do Enterprise Vault 15.1.</summary>
    public const string Documented151Id = "EV_EXPORT_PROFILE_DOCUMENTED_15_1";

    /// <summary>
    /// Perfil oficial documentado (EV 15.1): identifica-se por <c>ArchiveId</c> + <c>OutputDirectory</c> +
    /// <c>Format</c>; documenta ainda <c>SearchString</c>, <c>MaxThreads</c>, <c>Retry</c> e
    /// <c>MaxPSTSizeMB</c>. Maturidade: documentação oficial + validação por FIXTURES automatizados,
    /// **sem** homologação em laboratório (`LaboratoryValidated = false`).
    /// </summary>
    public static EvExportProfile Documented151 { get; } = new(
        Documented151Id,
        [EvExportParameters.ArchiveId, EvExportParameters.OutputDirectory, EvExportParameters.Format],
        [
            EvExportParameters.ArchiveId, EvExportParameters.OutputDirectory, EvExportParameters.SearchString,
            EvExportParameters.Format, EvExportParameters.MaxThreads, EvExportParameters.Retry, EvExportParameters.MaxPstSizeMb,
        ],
        new EvExportMaturity(
            RuntimeObserved: false, OfficialDocumentation: true, AutomatedFixtureValidated: true, LaboratoryValidated: false));
}
