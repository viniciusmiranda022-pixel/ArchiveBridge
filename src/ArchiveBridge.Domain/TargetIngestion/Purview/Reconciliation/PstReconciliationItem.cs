using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Disposition técnica explícita de UM PST — esperado (resolvido server-side pela cadeia canônica
/// <c>WaveEntry ↔ Binding ↔ PartitionExecution ↔ Upload manifest ↔ Mapping</c>) ou observado (presente no
/// service result do Purview mas fora do conjunto esperado — <see cref="ReconciliationDisposition.ExtraInProvider"/>),
/// nunca ambos ao mesmo tempo (AB-I6-007 itens 5/7). Contadores refletem exatamente os valores observados
/// (<see langword="null"/> quando o campo/linha correlacionada não existe — Unknown/NotReported, nunca
/// zero).
/// </summary>
public sealed record PstReconciliationItem
{
    /// <summary>Cria o item, validando que contadores fornecidos (quando não nulos) não são negativos.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Um contador fornecido é negativo.</exception>
    public PstReconciliationItem(
        PurviewRemotePstName remoteName,
        ReconciliationDisposition disposition,
        PurviewServiceResultRowStatus? observedStatus,
        long? importedItemCount,
        long? importedSizeBytes,
        long? skippedItemCount,
        long? corruptedItemCount)
    {
        RemoteName = remoteName;
        Disposition = disposition;
        ObservedStatus = observedStatus;
        ImportedItemCount = RequireNonNegativeOrNull(importedItemCount, nameof(importedItemCount));
        ImportedSizeBytes = RequireNonNegativeOrNull(importedSizeBytes, nameof(importedSizeBytes));
        SkippedItemCount = RequireNonNegativeOrNull(skippedItemCount, nameof(skippedItemCount));
        CorruptedItemCount = RequireNonNegativeOrNull(corruptedItemCount, nameof(corruptedItemCount));
    }

    /// <summary>Identidade de transporte (nome remoto de PST) — do lado esperado quando correlacionado, ou do lado observado quando <see cref="ReconciliationDisposition.ExtraInProvider"/>.</summary>
    public PurviewRemotePstName RemoteName { get; }

    /// <summary>Disposition técnica explícita deste item (nunca um resultado de reconciliação final).</summary>
    public ReconciliationDisposition Disposition { get; }

    /// <summary>Status normalizado observado, ou <see langword="null"/> quando nenhuma linha correlacionou (PST esperado ausente do provider).</summary>
    public PurviewServiceResultRowStatus? ObservedStatus { get; }

    /// <summary>Itens importados observados, ou <see langword="null"/> (Unknown/NotReported).</summary>
    public long? ImportedItemCount { get; }

    /// <summary>Bytes importados observados, ou <see langword="null"/> (Unknown/NotReported).</summary>
    public long? ImportedSizeBytes { get; }

    /// <summary>Itens ignorados observados, ou <see langword="null"/> (Unknown/NotReported).</summary>
    public long? SkippedItemCount { get; }

    /// <summary>Itens corrompidos observados, ou <see langword="null"/> (Unknown/NotReported).</summary>
    public long? CorruptedItemCount { get; }

    private static long? RequireNonNegativeOrNull(long? value, string parameterName)
    {
        if (value is { } present && present < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, present, "Um contador fornecido não pode ser negativo.");
        }

        return value;
    }
}
