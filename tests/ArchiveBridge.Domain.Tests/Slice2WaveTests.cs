using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Tests;

public sealed class Slice2WaveTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static WaveSelection Selection(params string[] pstNames)
    {
        var entries = pstNames
            .Select((pst, index) => new WaveEntry($"/src/{index}/{pst}", pst, new ArchiveRef($"user{index}@contoso.com"), 10, 1))
            .ToList();
        return new WaveSelection(entries);
    }

    private static MigrationWave NewWave(WaveSelection selection) =>
        MigrationWave.Create(
            WaveId.New(),
            new TenantId(Guid.NewGuid()),
            new ProjectId(Guid.NewGuid()),
            new WaveName("Onda 1"),
            TargetRootFolder.ForWave("ProjA", "Wave1"),
            new Sha256Hash("cfg-hash"),
            selection,
            Now);

    [Fact]
    public void CreateStartsInDraftVersionOneWithDerivedTotals()
    {
        var wave = NewWave(Selection("a.pst", "b.pst"));
        Assert.Equal(WaveStatus.Draft, wave.Status);
        Assert.Equal(1, wave.Version.Value);
        Assert.Equal(20, wave.PlannedBytes);
        Assert.Equal(2, wave.PlannedItems);
        Assert.Equal(wave.Selection.ComputeHash(), wave.SelectionHash);
    }

    [Fact]
    public void UpdateSelectionBumpsVersionAndRevertsToDraft()
    {
        var wave = NewWave(Selection("a.pst"));
        wave.StartValidation();
        wave.UpdateSelection(Selection("a.pst", "c.pst"));

        Assert.Equal(2, wave.Version.Value);
        Assert.Equal(WaveStatus.Draft, wave.Status);
        Assert.Equal(2, wave.PlannedItems);
    }

    [Fact]
    public void ApproveSetsApproverAndFreezesSelectionAndTarget()
    {
        var wave = NewWave(Selection("a.pst"));
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("decision.owner", Now);

        Assert.Equal(WaveStatus.Approved, wave.Status);
        Assert.Equal("decision.owner", wave.ApprovedBy);
        Assert.Equal(Now, wave.ApprovedAtUtc);

        Assert.Throws<InvalidWaveTransitionException>(() => wave.UpdateSelection(Selection("b.pst")));
        Assert.Throws<InvalidWaveTransitionException>(() => wave.ChangeTargetRootFolder(TargetRootFolder.ForWave("ProjA", "Wave2")));
    }

    [Fact]
    public void RetryPreservesSameTargetFolder()
    {
        var wave = NewWave(Selection("a.pst"));
        var folderBefore = wave.TargetRootFolder;
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("do", Now);
        wave.Freeze();
        // Um "retry" reidrata a mesma onda: o destino permanece idêntico.
        Assert.Equal(folderBefore, wave.TargetRootFolder);
    }

    [Fact]
    public void ApproveRequiresApprover()
    {
        var wave = NewWave(Selection("a.pst"));
        wave.StartValidation();
        wave.MarkReadyForApproval();
        Assert.Throws<ArgumentException>(() => wave.Approve("  ", Now));
    }

    [Fact]
    public void CannotApproveWithPendingValidations()
    {
        var wave = NewWave(Selection("a.pst"));
        // Direto de Draft não é possível aprovar (precisa passar por Validating → ReadyForApproval).
        Assert.Throws<InvalidWaveTransitionException>(() => wave.Approve("do", Now));
        wave.StartValidation();
        Assert.Throws<InvalidWaveTransitionException>(() => wave.Approve("do", Now));
    }

    [Theory]
    [InlineData(WaveStatus.Draft, WaveStatus.Validating)]
    [InlineData(WaveStatus.Validating, WaveStatus.Blocked)]
    [InlineData(WaveStatus.Validating, WaveStatus.ReadyForApproval)]
    [InlineData(WaveStatus.Blocked, WaveStatus.Validating)]
    [InlineData(WaveStatus.ReadyForApproval, WaveStatus.Approved)]
    [InlineData(WaveStatus.Approved, WaveStatus.Frozen)]
    [InlineData(WaveStatus.Frozen, WaveStatus.Completed)]
    public void AllowedWaveTransitions(WaveStatus from, WaveStatus to) =>
        Assert.True(WaveStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(WaveStatus.Draft, WaveStatus.Approved)]
    [InlineData(WaveStatus.ReadyForApproval, WaveStatus.Frozen)]
    [InlineData(WaveStatus.Approved, WaveStatus.Completed)]
    [InlineData(WaveStatus.Completed, WaveStatus.Frozen)]
    [InlineData(WaveStatus.Cancelled, WaveStatus.Draft)]
    public void DisallowedWaveTransitionsFailClosed(WaveStatus from, WaveStatus to)
    {
        Assert.False(WaveStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidWaveTransitionException>(() => WaveStateMachine.EnsureCanTransition(from, to));
    }
}
