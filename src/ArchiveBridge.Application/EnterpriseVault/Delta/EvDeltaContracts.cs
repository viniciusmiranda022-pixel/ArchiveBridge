using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>Resultado uniforme das solicitações de fase de delta (Baseline/Delta/FinalDelta) — a tentativa vigente e se foi um replay idempotente.</summary>
public sealed record EvDeltaRunResult(
    EvDeltaRunId Run, EvDeltaAttemptId Attempt, int AttemptNumber, EvDeltaRunOutcome Outcome, WatermarkId? IssuedWatermark, bool Replayed);
