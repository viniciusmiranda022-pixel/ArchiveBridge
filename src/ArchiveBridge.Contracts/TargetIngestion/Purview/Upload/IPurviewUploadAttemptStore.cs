using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;

/// <summary>
/// UMA tentativa de upload persistida (append-only, item 8/11) — nunca reescrita; a história completa é
/// sempre preservada. <see cref="IdentityHash"/> é a identidade lógica (item 14) calculada NESTA tentativa
/// a partir do conjunto de bindings/SAS/binário/prefixo então vigentes — compará-la com a de uma tentativa
/// <see cref="PurviewUploadAttemptOutcome.Uploaded"/> anterior é como o processador decide entre réplay
/// idempotente (mesma identidade) e uma execução genuinamente nova (identidade diferente).
/// <see cref="Evidence"/> só é não nula quando <see cref="Outcome"/> é
/// <see cref="PurviewUploadAttemptOutcome.Uploaded"/>.
/// </summary>
public sealed record PurviewUploadAttemptRecord(
    PurviewUploadRequestId Request,
    PurviewUploadAttemptId Attempt,
    int AttemptNumber,
    Sha256Hash IdentityHash,
    PurviewUploadAttemptOutcome Outcome,
    string? BlockingReason,
    PurviewUploadEvidence? Evidence,
    int? ProcessExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Store append-only da história de tentativas de upload (AB-I5-009 items 8/10/11/14): cada
/// <see cref="AppendAsync"/> grava UMA nova linha imutável — a evidência de uma tentativa anterior NUNCA é
/// reescrita. Toda leitura é escopada (anti-IDOR): um pedido fora do escopo autenticado é indistinguível de
/// inexistente.
/// </summary>
public interface IPurviewUploadAttemptStore
{
    /// <summary>Persiste uma nova tentativa (append). Sob fencing quando <paramref name="fence"/> não é nulo (item 9).</summary>
    Task AppendAsync(TenantScope scope, PurviewUploadAttemptRecord record, JobFence? fence, CancellationToken cancellationToken);

    /// <summary>Devolve a tentativa mais recente do pedido no escopo; <see langword="null"/> se nenhuma existir.</summary>
    Task<PurviewUploadAttemptRecord?> GetLatestAsync(TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve a tentativa mais recente (por <see cref="PurviewUploadAttemptRecord.CompletedAtUtc"/>) dentre
    /// TODOS os pedidos de upload já registrados neste tenant/projeto — <see langword="null"/> se nenhuma
    /// tentativa existir. Usado pelo Production Readiness Review (AB-I8-002), que é escopado a tenant/
    /// projeto, não a uma onda/pedido específico; nunca filtra por <see cref="PurviewUploadRequestId"/>.
    /// </summary>
    Task<PurviewUploadAttemptRecord?> GetLatestAcrossRequestsAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Devolve TODA a história de tentativas do pedido, em ordem de <c>AttemptNumber</c> crescente (evidência/auditoria).</summary>
    Task<IReadOnlyList<PurviewUploadAttemptRecord>> ListAttemptsAsync(
        TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken);
}
