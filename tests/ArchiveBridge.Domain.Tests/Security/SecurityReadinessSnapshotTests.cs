using ArchiveBridge.Domain.Security;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Security;

/// <summary>
/// AB-I7-008 item 7/acceptance criteria — <see cref="SecurityReadinessSnapshot"/>: agrega apenas o que
/// foi realmente resolvido pelo chamador e NUNCA expõe/deriva um status agregado de "Production Ready".
/// </summary>
public sealed class SecurityReadinessSnapshotTests
{
    [Fact]
    public void ComposeCarriesOverExactlyTheSuppliedPerCategoryStatuses()
    {
        var workerHardening = new Dictionary<WorkerHardeningControl, WorkerHardeningStatus>
        {
            [WorkerHardeningControl.BitLocker] = WorkerHardeningStatus.NotMeasured,
            [WorkerHardeningControl.MdeTenantPolicyEnforcement] = WorkerHardeningStatus.Blocked,
        };
        var incidentDrills = new Dictionary<IncidentResponseDrillType, IncidentResponseDrillOutcome>
        {
            [IncidentResponseDrillType.SecretLeakCanary] = IncidentResponseDrillOutcome.Contained,
        };

        var snapshot = SecurityReadinessSnapshot.Compose(
            workerHardening, latestWdacPolicy: null, latestBuildProvenanceByArtifact: [], incidentDrills,
            PenTestReadinessStatus.NotPerformed);

        Assert.Equal(WorkerHardeningStatus.NotMeasured, snapshot.WorkerHardeningControls[WorkerHardeningControl.BitLocker]);
        Assert.Equal(WorkerHardeningStatus.Blocked, snapshot.WorkerHardeningControls[WorkerHardeningControl.MdeTenantPolicyEnforcement]);
        Assert.Equal(IncidentResponseDrillOutcome.Contained, snapshot.LatestIncidentResponseDrills[IncidentResponseDrillType.SecretLeakCanary]);
        Assert.Equal(PenTestReadinessStatus.NotPerformed, snapshot.PenTestStatus);
        Assert.Null(snapshot.LatestWdacPolicy);
        Assert.Empty(snapshot.LatestBuildProvenanceByArtifact);
    }

    [Fact]
    public void TheDisclaimerNeverClaimsProductionReadinessAndIsAlwaysPresent()
    {
        Assert.Contains("NEVER", SecurityReadinessSnapshot.Disclaimer, StringComparison.Ordinal);
        Assert.Contains("Production Read", SecurityReadinessSnapshot.Disclaimer, StringComparison.Ordinal);
    }
}
