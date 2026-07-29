using ArchiveBridge.Domain.Evidence;

namespace ArchiveBridge.Contracts.Evidence;

/// <summary>
/// Porta da cadeia de custódia (trilha de evidência). Append-only; assinatura de evidência e
/// WORM entram por slice posterior (ADR pendente). Implementada na Infrastructure.
/// </summary>
public interface IEvidenceLedger
{
    /// <summary>Anexa um evento de custódia encadeado à trilha do projeto/wave.</summary>
    Task AppendAsync(CustodyEvent custodyEvent, CancellationToken cancellationToken);
}
