namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>
/// Resultado AVALIADO de UMA tentativa de exportação (antes da persistência) — espelha
/// <c>EvDiscoveryRunResult</c> para o mesmo nível de rigor. <see cref="Manifest"/> só é não-nulo quando
/// <see cref="Outcome"/> é <see cref="EvExportAttemptOutcome.Completed"/>; <see cref="OversizedItems"/> pode
/// ser não-vazio em qualquer desfecho pós-execução (o exporter pode reportar itens oversized mesmo quando a
/// tentativa falha por outro motivo) — nunca omitido do resultado avaliado.
/// </summary>
public sealed record EvExportRunResult(
    ExportRequestId Request,
    ExportAttemptId Attempt,
    int AttemptNumber,
    EvExportAttemptOutcome Outcome,
    EvExportResultCode ResultCode,
    string? BlockingReason,
    EvExportManifest? Manifest,
    IReadOnlyList<EvOversizedNativeItem> OversizedItems,
    int? ProcessExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    /// <summary>Verdadeiro quando a tentativa concluiu com sucesso e possui manifesto canônico.</summary>
    public bool IsSuccessful => Outcome == EvExportAttemptOutcome.Completed && Manifest is not null;
}
