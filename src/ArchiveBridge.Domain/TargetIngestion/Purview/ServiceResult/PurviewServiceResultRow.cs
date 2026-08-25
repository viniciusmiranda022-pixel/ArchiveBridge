using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Estado por PST normalizado a partir do validation report / service result do Purview (runbook §26.1).
/// <see cref="Unknown"/> é o padrão quando o serviço não fornece um status reconhecido — NUNCA inferido
/// como sucesso (AB-I6-001 item 7: "campo não fornecido permanece Unknown/NotReported").
/// </summary>
public enum PurviewServiceResultRowStatus
{
    /// <summary>Nenhum status reconhecido foi fornecido para este PST.</summary>
    Unknown,

    /// <summary>O serviço reportou sucesso para este PST.</summary>
    Succeeded,

    /// <summary>O serviço reportou falha para este PST.</summary>
    Failed,

    /// <summary>O serviço reportou itens ignorados/corrompidos para este PST.</summary>
    SkippedOrCorrupted,
}

/// <summary>
/// UMA linha normalizada do validation report / service result do Purview, já correlacionada por
/// <see cref="PurviewRemotePstName"/> (AB-I6-001 itens 7-8). Cada contador é <see langword="null"/>
/// (Unknown/NotReported) quando o campo correspondente não foi efetivamente fornecido pelo serviço —
/// NUNCA convertido para zero (item 7). A linha nunca carrega mailbox/UPN, caminho local, conteúdo de
/// mensagem ou qualquer segredo — apenas a identidade de transporte (nome remoto) e os contadores
/// agregados já expostos pelo próprio relatório.
/// </summary>
public sealed record PurviewServiceResultRow
{
    /// <summary>Cria a linha, validando que os contadores fornecidos (quando não nulos) não são negativos.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Um contador fornecido é negativo.</exception>
    public PurviewServiceResultRow(
        PurviewRemotePstName remoteName,
        PurviewServiceResultRowStatus status,
        long? importedItemCount,
        long? importedSizeBytes,
        long? skippedItemCount,
        long? corruptedItemCount)
    {
        RemoteName = remoteName;
        Status = status;
        ImportedItemCount = RequireNonNegativeOrNull(importedItemCount, nameof(importedItemCount));
        ImportedSizeBytes = RequireNonNegativeOrNull(importedSizeBytes, nameof(importedSizeBytes));
        SkippedItemCount = RequireNonNegativeOrNull(skippedItemCount, nameof(skippedItemCount));
        CorruptedItemCount = RequireNonNegativeOrNull(corruptedItemCount, nameof(corruptedItemCount));
    }

    /// <summary>Nome de arquivo remoto EXATO usado pelo transporte real — chave de correlação 1:1 com a cadeia canônica.</summary>
    public PurviewRemotePstName RemoteName { get; }

    /// <summary>Status normalizado (<see cref="PurviewServiceResultRowStatus.Unknown"/> quando não reconhecido).</summary>
    public PurviewServiceResultRowStatus Status { get; }

    /// <summary>Quantidade de itens importados, ou <see langword="null"/> quando não fornecida (Unknown/NotReported).</summary>
    public long? ImportedItemCount { get; }

    /// <summary>Tamanho importado em bytes, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public long? ImportedSizeBytes { get; }

    /// <summary>Quantidade de itens ignorados, ou <see langword="null"/> quando não fornecida (Unknown/NotReported).</summary>
    public long? SkippedItemCount { get; }

    /// <summary>Quantidade de itens corrompidos, ou <see langword="null"/> quando não fornecida (Unknown/NotReported).</summary>
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
