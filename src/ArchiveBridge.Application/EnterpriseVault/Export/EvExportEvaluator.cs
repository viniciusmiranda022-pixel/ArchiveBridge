using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Application.EnterpriseVault.Export;

/// <summary>
/// Montagem PURA (sem I/O) de <see cref="EvExportRunResult"/> a partir das peças brutas de UMA tentativa —
/// mantém o processor enxuto e testável sem executar processo/SQL/filesystem algum.
/// </summary>
public static class EvExportEvaluator
{
    /// <summary>Monta o resultado de sucesso: manifesto canônico + itens oversized classificados.</summary>
    public static EvExportRunResult EvaluateSuccess(
        ExportRequestId request, ExportAttemptId attempt, int attemptNumber, string externalArchiveId,
        EvExportProcessResult process, IReadOnlyList<EvExportOutputCandidate> candidates,
        DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc)
    {
        var entries = candidates
            .Select(candidate => new EvExportManifestEntry(candidate.FileNameHint, candidate.SizeBytes, candidate.ContentSha256))
            .ToArray();
        var manifest = EvExportManifest.Create(request, attempt, externalArchiveId, entries, process.EngineVersion, process.ConnectorVersion);
        var oversized = BuildOversizedItems(process.OversizedItemsRaw, completedAtUtc);

        return new EvExportRunResult(
            request, attempt, attemptNumber, EvExportAttemptOutcome.Completed,
            new EvExportResultCode(EvExportResultCodes.ExportCompleted), null, manifest, oversized,
            process.ExitCode, startedAtUtc, completedAtUtc);
    }

    /// <summary>Monta o resultado de falha do PROCESSO (candidata a retry transitório) — timeout, limite de saída ou exit code não zero.</summary>
    public static EvExportRunResult EvaluateProcessFailure(
        ExportRequestId request, ExportAttemptId attempt, int attemptNumber,
        EvExportProcessResult process, DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc)
    {
        var code = process switch
        {
            { TimedOut: true } => EvExportResultCodes.ExportTimeout,
            { OutputLimitExceeded: true } => EvExportResultCodes.ExportOutputLimitExceeded,
            _ => EvExportResultCodes.ExportFailed,
        };
        var oversized = BuildOversizedItems(process.OversizedItemsRaw, completedAtUtc);

        return new EvExportRunResult(
            request, attempt, attemptNumber, EvExportAttemptOutcome.Failed,
            new EvExportResultCode(code), process.ErrorCode, Manifest: null, oversized,
            process.ExitCode, startedAtUtc, completedAtUtc);
    }

    /// <summary>Monta o resultado de bloqueio TERMINAL por capability ausente/revogada (item 8) — nunca invoca o executor.</summary>
    public static EvExportRunResult EvaluateCapabilityBlocked(
        ExportRequestId request, ExportAttemptId attempt, int attemptNumber, string blockingReason, DateTimeOffset nowUtc) =>
        new(
            request, attempt, attemptNumber, EvExportAttemptOutcome.CapabilityBlocked,
            new EvExportResultCode(EvExportResultCodes.CapabilityNotCertified), blockingReason, Manifest: null,
            [], ProcessExitCode: null, nowUtc, nowUtc);

    /// <summary>Monta o resultado de bloqueio TRANSITÓRIO por concorrência não autorizada (item 4) — nunca invoca o executor.</summary>
    public static EvExportRunResult EvaluateThrottled(
        ExportRequestId request, ExportAttemptId attempt, int attemptNumber, bool byConnector, DateTimeOffset nowUtc) =>
        new(
            request, attempt, attemptNumber, EvExportAttemptOutcome.Throttled,
            new EvExportResultCode(byConnector ? EvExportResultCodes.ThrottledConnector : EvExportResultCodes.ThrottledArchive),
            null, Manifest: null, [], ProcessExitCode: null, nowUtc, nowUtc);

    /// <summary>Monta o resultado de falha de INTEGRIDADE (containment/revalidação, item 9/13) — terminal, fail-closed.</summary>
    public static EvExportRunResult EvaluateIntegrityFailed(
        ExportRequestId request, ExportAttemptId attempt, int attemptNumber, string resultCode, string reason,
        DateTimeOffset startedAtUtc, DateTimeOffset nowUtc) =>
        new(
            request, attempt, attemptNumber, EvExportAttemptOutcome.IntegrityFailed,
            new EvExportResultCode(resultCode), reason, Manifest: null, [], ProcessExitCode: null, startedAtUtc, nowUtc);

    private static EvOversizedNativeItem[] BuildOversizedItems(
        IReadOnlyList<EvOversizedNativeItemRaw> raw, DateTimeOffset detectedAtUtc) =>
        raw
            .Select(item => (Item: item, Classification: EvOversizedNativeItem.Classify(item.SizeBytes)))
            .Where(pair => pair.Classification is not null)
            .Select(pair => new EvOversizedNativeItem(
                pair.Item.ExternalItemReference, pair.Item.SizeBytes, pair.Classification!.Value, pair.Item.Diagnostic, detectedAtUtc))
            .ToArray();
}
