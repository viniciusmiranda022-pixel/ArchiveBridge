using ArchiveBridge.Domain.Security;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-008 item 5/acceptance criteria 6 (SQL Server real) — os TRÊS drills de incident-response
/// sintéticos e não destrutivos exigidos pelo work order, cada um produzindo um
/// <see cref="IncidentResponseDrillRecord"/> auditável, sem segredo/PII persistido e sem efeito externo.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class IncidentResponseDrillHarnessIntegrationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheSecretLeakCanaryDrillProducesContainedEvidenceWithNoRawSecretPersisted()
    {
        var harness = new IncidentResponseDrillHarness(fixture);
        var scope = SqlServerFixture.NewScope();

        var record = await harness.RunSecretLeakCanaryAsync(scope, Start, CancellationToken.None);

        Assert.Equal(IncidentResponseDrillOutcome.Contained, record.Outcome);
        Assert.DoesNotContain("canary-drill-secret-DO-NOT-PERSIST-999", record.Disposition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHashMismatchTamperingDrillProducesContainedEvidenceWhenIntegrityRevalidationRejectsTheTamperedRow()
    {
        var harness = new IncidentResponseDrillHarness(fixture);
        var scope = SqlServerFixture.NewScope();

        var record = await harness.RunHashMismatchTamperingAsync(scope, Start, CancellationToken.None);

        Assert.Equal(IncidentResponseDrillOutcome.Contained, record.Outcome);
    }

    [Fact]
    public async Task TheCrossTenantDenialDrillProducesContainedEvidenceWhenRlsDeniesTheOtherTenantsRow()
    {
        var harness = new IncidentResponseDrillHarness(fixture);
        var drillTenantScope = SqlServerFixture.NewScope();
        var otherTenantScope = SqlServerFixture.NewScope();

        var record = await harness.RunCrossTenantDenialAsync(drillTenantScope, otherTenantScope, Start, CancellationToken.None);

        Assert.Equal(IncidentResponseDrillOutcome.Contained, record.Outcome);
    }
}
