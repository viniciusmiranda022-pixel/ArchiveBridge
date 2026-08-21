using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Metadados de execução fornecidos pela Application ao <see cref="IPartitionPartWriter"/> ANTES de
/// qualquer materialização (Slice 4B, Passo 3 — correção AB-4B-008, work order AB-4B-006 item 7: o
/// manifesto durável precisa carregar <c>correlation_id</c>/<c>started_at_utc</c> como evidência local
/// autoconsistente, sem depender exclusivamente do banco).
/// <para>
/// <see cref="Correlation"/> e <see cref="StartedAtUtc"/> só são conhecidos pela Application — o writer
/// nunca os inventa. Fornecê-los ANTES da cópia (em vez de "corrigir" o manifesto depois que o bundle já
/// foi publicado) preserva o protocolo de imutabilidade: o bundle final é publicado UMA ÚNICA VEZ já
/// completo, nunca reescrito. <c>completed_at_utc</c> continua sendo determinado pelo PRÓPRIO writer, no
/// instante em que o bundle é considerado pronto para publicação (ele é quem sabe quando a verificação
/// terminou) — nunca escrito de volta pela Application depois do fato.
/// </para>
/// </summary>
public sealed record PartitionExecutionContext(CorrelationId Correlation, DateTimeOffset StartedAtUtc);
