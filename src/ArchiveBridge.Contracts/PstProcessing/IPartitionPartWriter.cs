using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>Resultado normalizado e VERIFICADO da materialização de uma parte: hash/tamanho do output REALMENTE
/// escrito e confirmado por reabertura/reinspeção antes de retornar. Nunca carrega caminho físico.</summary>
public sealed record PartitionPartArtifact(Sha256Hash OutputHash, long OutputSizeBytes);

/// <summary>
/// Porta do writer de partição (Slice 4B, Passo 3) — a fronteira que torna a materialização de output
/// SUBSTITUÍVEL sem tocar Domain/Application, exatamente como <see cref="IPartitionPlanner"/> faz para o
/// planejamento e <see cref="IPstEngine"/> faz para a inspeção. Implementações são responsáveis por TODO o
/// protocolo de segurança do work order AB-4B-006: source sempre read-only, output só canônico após write
/// completo + flush/close + hash + reabertura/reinspeção, containment/anti-symlink em source e output,
/// preflight de espaço, honra a <see cref="CancellationToken"/>, e idempotência via convergência no MESMO
/// caminho determinístico de output (derivado de IDs opacos) — nunca sobrescreve um output existente que
/// diverge do esperado (fail-closed, ver <see cref="PartitionExecutionOutputTamperedException"/>).
/// </summary>
public interface IPartitionPartWriter
{
    /// <summary>Nome estável do executor (participa da evidência/auditoria).</summary>
    string ExecutorName { get; }

    /// <summary>Versão do executor (evidência/auditoria).</summary>
    string ExecutorVersion { get; }

    /// <summary>
    /// Materializa (ou reaproveita idempotentemente, se o output canônico já existe e confere) a
    /// <paramref name="part"/> do <paramref name="plan"/> a partir da <paramref name="source"/> sob custódia,
    /// devolvendo o resultado apenas depois de verificado. Neste Passo, suporta exclusivamente partes que
    /// cobrem o artefato de origem inteiro (<see cref="PartitionPlanPart.CoversEntireSource"/>).
    /// </summary>
    /// <exception cref="PartitionExecutionSourceStaleException">A origem mudou durante a materialização.</exception>
    /// <exception cref="PartitionExecutionOutputTamperedException">
    /// Um output já existente no caminho canônico não confere com o esperado (nunca sobrescrito).
    /// </exception>
    /// <exception cref="PartitionExecutionLimitExceededException">Espaço em disco insuficiente ou timeout.</exception>
    Task<PartitionPartArtifact> ExecuteAsync(
        TenantScope scope,
        MigrationArtifact source,
        PartitionPlan plan,
        PartitionPlanPart part,
        CancellationToken cancellationToken);
}
