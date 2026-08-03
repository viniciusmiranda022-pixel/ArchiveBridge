using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B6: decisão de avaliação Microsoft (append-only). Uma onda acima de 100 GB fica bloqueada; somente
/// uma decisão APROVADA, da versão corrente e do archive avaliado, libera a onda (Blocked → Validating)
/// e permite revalidação. Decisão de outra versão / outro archive / sem responsável é recusada.
/// Cross-projeto é barrado pelo escopo.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2AssessmentDecisionTests(SqlServerFixture fixture)
{
    private const long Over = CapacityRule.OneHundredGigabytesInBytes + 1;
    private const long Under = 10;

    private static TargetArchiveId Archive(string mailbox) => new(mailbox);

    private static AssessmentDecisionCommand Approve(
        TenantScope scope, WaveId wave, int version, string archiveMailbox, string by = "decision.owner") =>
        new(scope, wave, version, Archive(archiveMailbox), MicrosoftAssessmentDecision.Approved, "EV-123", by, CorrelationId.New(), "sem PII");

    private async Task<MigrationWave> BlockedWaveAsync(WaveSelection selection)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, selection);
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        var result = await Slice2Support.ValidateWave(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(new TenantScope(wave.Tenant, wave.Project), wave.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(WaveStatus.Blocked, result.Status);
        return wave;
    }

    [Fact]
    public async Task WaveStaysBlockedWithoutDecision()
    {
        var wave = await BlockedWaveAsync(new WaveSelection([Slice2Support.Entry("a.pst", "over@contoso.com", Over)]));
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var revalidated = await Slice2Support.ValidateWave(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(WaveStatus.Blocked, revalidated.Status);
    }

    [Fact]
    public async Task ValidDecisionAllowsRevalidationToReadyForApproval()
    {
        var wave = await BlockedWaveAsync(new WaveSelection([Slice2Support.Entry("a.pst", "over@contoso.com", Over)]));
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var result = await Slice2Support.RecordDecision(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(Approve(scope, wave.Id, 1, "over@contoso.com"), CancellationToken.None);
        Assert.Equal(WaveStatus.ReadyForApproval, result.Status);
    }

    [Fact]
    public async Task DecisionOfAnotherVersionIsRejected()
    {
        var wave = await BlockedWaveAsync(new WaveSelection([Slice2Support.Entry("a.pst", "over@contoso.com", Over)]));
        var scope = new TenantScope(wave.Tenant, wave.Project);
        await Assert.ThrowsAsync<PlanningValidationException>(() =>
            Slice2Support.RecordDecision(fixture, new MutableClock(Slice2Support.Now))
                .ExecuteAsync(Approve(scope, wave.Id, 999, "over@contoso.com"), CancellationToken.None));
    }

    [Fact]
    public async Task DecisionWithoutResponsibleIsRejected()
    {
        var wave = await BlockedWaveAsync(new WaveSelection([Slice2Support.Entry("a.pst", "over@contoso.com", Over)]));
        var scope = new TenantScope(wave.Tenant, wave.Project);
        await Assert.ThrowsAsync<PlanningValidationException>(() =>
            Slice2Support.RecordDecision(fixture, new MutableClock(Slice2Support.Now))
                .ExecuteAsync(Approve(scope, wave.Id, 1, "over@contoso.com", by: "   "), CancellationToken.None));
    }

    [Fact]
    public async Task DecisionForAnotherArchiveDoesNotRelease()
    {
        // Onda com A acima do limite e B abaixo; aprovar B não libera A.
        var wave = await BlockedWaveAsync(new WaveSelection([
            Slice2Support.Entry("a.pst", "over@contoso.com", Over),
            Slice2Support.Entry("b.pst", "under@contoso.com", Under),
        ]));
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var result = await Slice2Support.RecordDecision(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(Approve(scope, wave.Id, 1, "under@contoso.com"), CancellationToken.None);
        Assert.Equal(WaveStatus.Blocked, result.Status);
    }

    [Fact]
    public async Task CrossProjectDecisionIsRejected()
    {
        var wave = await BlockedWaveAsync(new WaveSelection([Slice2Support.Entry("a.pst", "over@contoso.com", Over)]));
        var otherScope = new TenantScope(wave.Tenant, new ProjectId(Guid.NewGuid()));
        await Assert.ThrowsAsync<PlanningNotFoundException>(() =>
            Slice2Support.RecordDecision(fixture, new MutableClock(Slice2Support.Now))
                .ExecuteAsync(Approve(otherScope, wave.Id, 1, "over@contoso.com"), CancellationToken.None));
    }
}
