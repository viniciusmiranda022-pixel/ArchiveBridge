using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Resultado normalizado da inspeção de uma part PST: contagens e estrutura, sem tipos do
/// fornecedor. É o único formato que atravessa a fronteira do domínio (§18.2).
/// </summary>
public sealed record PstInspectionResult(long ItemCount, int FolderCount);

/// <summary>
/// Porta da engine PST (§18.2). Nenhum tipo de biblioteca de leitura (Aspose, libpff) cruza
/// esta fronteira — o domínio recebe apenas <see cref="PstInspectionResult"/> normalizado.
/// A validação independente por libpff é capacidade opcional fora do MVP (ADR-0005).
/// </summary>
public interface IPstEngine
{
    /// <summary>
    /// Abre e inspeciona uma part, devolvendo o resultado normalizado. Reabertura após o writer
    /// e reconciliação são fail-closed: inconsistência bloqueia, não passa.
    /// </summary>
    Task<PstInspectionResult> InspectAsync(ArtifactId artifact, CancellationToken cancellationToken);
}
