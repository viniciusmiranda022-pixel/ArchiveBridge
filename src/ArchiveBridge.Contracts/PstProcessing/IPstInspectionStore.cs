using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Persistência append-only das tentativas de inspeção (checkpoint do Passo 1). <see cref="FindCanonicalAsync"/>
/// só retorna uma tentativa <c>Completed</c> cujo hash observado bateu com <paramref name="expectedHash"/> —
/// a base do réplay idempotente. <see cref="SaveAsync"/> lança <see cref="PstInspectionConflictException"/>
/// quando o índice único filtrado de canônicos (tenant, projeto, artefato, hash) já foi ocupado por uma
/// corrida concorrente; o chamador deve reler via <see cref="FindCanonicalAsync"/> (nunca tratar como erro
/// de negócio).
/// </summary>
public interface IPstInspectionStore
{
    /// <summary>Resultado canônico atual para o artefato+hash esperado, se houver.</summary>
    Task<PstInspectionRecord?> FindCanonicalAsync(
        TenantScope scope, ArtifactId artifact, Sha256Hash expectedHash, CancellationToken cancellationToken);

    /// <summary>Persiste uma nova tentativa (append-only).</summary>
    /// <exception cref="PstInspectionConflictException">Corrida concorrente já gravou o canônico equivalente.</exception>
    Task<PstInspectionRecord> SaveAsync(PstInspectionRecord record, CancellationToken cancellationToken);
}
