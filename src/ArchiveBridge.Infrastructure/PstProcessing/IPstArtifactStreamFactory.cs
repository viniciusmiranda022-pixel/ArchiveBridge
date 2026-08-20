namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Seam de abertura do stream físico de um artefato PST, usado por <see cref="HeaderOnlyPstInspectionEngine"/>.
/// Existe SOMENTE para permitir que testes de infraestrutura provem que
/// <see cref="PstStorageOptions.MaxSizeBytes"/> é reforçado sobre o stream EFETIVAMENTE lido — não apenas
/// sobre a metadata de <see cref="FileInfo"/> consultada antes da abertura (AB-4B-003). O composition root
/// de produção NUNCA injeta uma implementação alternativa: sempre usa <see cref="PhysicalPstArtifactStreamFactory"/>.
/// </summary>
internal interface IPstArtifactStreamFactory
{
    /// <summary>Abre <paramref name="absolutePath"/> somente leitura. A engine é responsável por descartar o stream retornado.</summary>
    Stream OpenRead(string absolutePath);
}

/// <summary>
/// Implementação real de <see cref="IPstArtifactStreamFactory"/>: abre o arquivo com o MESMO modo/
/// compartilhamento/opções usados antes de AB-4B-003 (somente leitura, compartilhado para leitura, streaming
/// assíncrono sequencial) — nenhuma mudança de comportamento em produção.
/// </summary>
internal sealed class PhysicalPstArtifactStreamFactory : IPstArtifactStreamFactory
{
    /// <summary>Instância única — a factory não tem estado.</summary>
    public static readonly PhysicalPstArtifactStreamFactory Instance = new();

    /// <inheritdoc />
    public Stream OpenRead(string absolutePath) => new FileStream(
        absolutePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        HeaderOnlyPstInspectionEngine.StreamBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
