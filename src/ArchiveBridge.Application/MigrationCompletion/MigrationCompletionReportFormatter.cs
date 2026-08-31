using ArchiveBridge.Contracts.MigrationCompletion;
using ArchiveBridge.Domain.MigrationCompletion;

namespace ArchiveBridge.Application.MigrationCompletion;

/// <summary>Projeta uma <see cref="MigrationCompletionAssessment"/> em <see cref="MigrationCompletionReportView"/> sanitizado (AB-I8-010, escopo obrigatório item 12).</summary>
internal static class MigrationCompletionReportFormatter
{
    public static MigrationCompletionReportView ToReportView(MigrationCompletionAssessment assessment, bool isCurrent, DateTimeOffset generatedAtUtc)
    {
        var criteria = assessment.CriterionResults
            .Select(result => new MigrationCompletionCriterionView(
                result.CriterionId.Value, result.Status, result.Evidence.Locator, result.ReasonCode, result.ObservedAtUtc))
            .ToList();

        var blockerSummaries = assessment.Blockers
            .Select(blocker => $"{blocker.CriterionId.Value}: {blocker.Status} ({blocker.ReasonCode})")
            .ToList();

        return new MigrationCompletionReportView(
            assessment.AssessmentVersion, assessment.Outcome, isCurrent, criteria, blockerSummaries, generatedAtUtc);
    }
}
