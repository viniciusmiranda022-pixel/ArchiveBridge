using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>UMA tentativa persistida (append-only) — nunca reescrita; a história completa é sempre preservada (item 11).</summary>
public sealed record EvExportAttemptRecord(
    ExportRequestId Request,
    ExportAttemptId Attempt,
    int AttemptNumber,
    ConnectorId Connector,
    string ExternalArchiveId,
    EvExportAttemptOutcome Outcome,
    EvExportResultCode ResultCode,
    string? BlockingReason,
    EvExportManifest? Manifest,
    IReadOnlyList<EvOversizedNativeItem> OversizedItems,
    int? ProcessExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Store append-only da história de tentativas de exportação (AB-4C-005 items 11/12/14): cada
/// <see cref="AppendAsync"/> grava UMA nova linha imutável, indexada por <c>ExportAttemptId</c> — a
/// evidência de uma tentativa anterior NUNCA é reescrita. Toda leitura é escopada (anti-IDOR): um pedido
/// fora do escopo autenticado é indistinguível de inexistente.
/// </summary>
public interface IEvExportAttemptStore
{
    /// <summary>Persiste uma nova tentativa (append). Sob fencing quando <paramref name="fence"/> não é nulo.</summary>
    Task AppendAsync(TenantScope scope, EvExportAttemptRecord record, JobFence? fence, CancellationToken cancellationToken);

    /// <summary>Devolve a tentativa mais recente do pedido no escopo; <see langword="null"/> se nenhuma existir.</summary>
    Task<EvExportAttemptRecord?> GetLatestAsync(TenantScope scope, ExportRequestId request, CancellationToken cancellationToken);

    /// <summary>Devolve TODA a história de tentativas do pedido, em ordem de <c>AttemptNumber</c> crescente (evidência/auditoria).</summary>
    Task<IReadOnlyList<EvExportAttemptRecord>> ListAttemptsAsync(
        TenantScope scope, ExportRequestId request, CancellationToken cancellationToken);
}
