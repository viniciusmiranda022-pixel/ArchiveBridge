using ArchiveBridge.Contracts.GoLive;
using ArchiveBridge.Domain.GoLive;

namespace ArchiveBridge.Application.GoLive;

/// <summary>Projeta uma <see cref="GoLiveAuthorizationDecision"/> em <see cref="GoLiveReportView"/> sanitizado (AB-I8-010, escopo obrigatório item 12).</summary>
internal static class GoLiveReportFormatter
{
    public static GoLiveReportView ToReportView(GoLiveAuthorizationDecision decision, bool isCurrent, DateTimeOffset generatedAtUtc)
    {
        var controls = decision.OperationalControlResults
            .Select(result => new GoLiveOperationalControlView(
                result.ControlId.Value, result.Group, result.Status, result.Evidence.Locator, result.ReasonCode, result.ObservedAtUtc))
            .ToList();

        var blockerSummaries = decision.Blockers
            .Select(blocker => $"{blocker.Code}: {blocker.ReasonCode}")
            .ToList();

        return new GoLiveReportView(
            decision.AuthorizationVersion, decision.BuildCommitSha, decision.Outcome, isCurrent, controls, blockerSummaries, generatedAtUtc);
    }
}
