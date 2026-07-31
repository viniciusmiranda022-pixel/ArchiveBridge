using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Tests;

public sealed class Slice2ProjectTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static MigrationProject NewProject() =>
        MigrationProject.Create(
            new ProjectId(Guid.NewGuid()),
            new TenantId(Guid.NewGuid()),
            new ProjectName("Projeto A"),
            new ProjectOwner("owner@contoso.com"),
            new ProjectConfiguration(new TargetTenant("contoso.onmicrosoft.com"), TargetArchivePolicy.OnlineArchive),
            Now);

    [Fact]
    public void CreateStartsInDraftWithVersionOneAndDeterministicHash()
    {
        var project = NewProject();
        Assert.Equal(ProjectStatus.Draft, project.Status);
        Assert.Equal(1, project.ConfigurationVersion.Value);
        Assert.Equal(project.Configuration.ComputeHash(), project.ConfigurationHash);
    }

    [Fact]
    public void ConfigurationHashIsDeterministicAndSensitiveToPolicy()
    {
        var tenant = new TargetTenant("contoso.onmicrosoft.com");
        var a = new ProjectConfiguration(tenant, TargetArchivePolicy.OnlineArchive);
        var b = new ProjectConfiguration(tenant, TargetArchivePolicy.OnlineArchive);
        var c = new ProjectConfiguration(tenant, TargetArchivePolicy.PrimaryMailbox);

        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), c.ComputeHash());
    }

    [Fact]
    public void UpdateConfigurationBumpsVersionRecomputesHashAndRevertsToDraft()
    {
        var project = NewProject();
        project.SubmitForAssessment(Now);
        Assert.Equal(ProjectStatus.ReadyForAssessment, project.Status);

        var newConfig = new ProjectConfiguration(new TargetTenant("fabrikam.onmicrosoft.com"), TargetArchivePolicy.PrimaryMailbox);
        project.UpdateConfiguration(newConfig, Later);

        Assert.Equal(2, project.ConfigurationVersion.Value);
        Assert.Equal(newConfig.ComputeHash(), project.ConfigurationHash);
        Assert.Equal(ProjectStatus.Draft, project.Status);
        Assert.Equal(Later, project.UpdatedAtUtc);
    }

    [Fact]
    public void ApprovedConfigurationIsFrozen()
    {
        var project = NewProject();
        project.SubmitForAssessment(Now);
        project.Approve(Later);
        Assert.Equal(ProjectStatus.Approved, project.Status);

        var config = new ProjectConfiguration(new TargetTenant("fabrikam.onmicrosoft.com"), TargetArchivePolicy.OnlineArchive);
        Assert.Throws<InvalidProjectTransitionException>(() => project.UpdateConfiguration(config, Later));
    }

    [Theory]
    [InlineData(ProjectStatus.Draft, ProjectStatus.ReadyForAssessment)]
    [InlineData(ProjectStatus.ReadyForAssessment, ProjectStatus.Approved)]
    [InlineData(ProjectStatus.ReadyForAssessment, ProjectStatus.Blocked)]
    [InlineData(ProjectStatus.Blocked, ProjectStatus.ReadyForAssessment)]
    [InlineData(ProjectStatus.Approved, ProjectStatus.Active)]
    [InlineData(ProjectStatus.Active, ProjectStatus.Completed)]
    public void AllowedProjectTransitions(ProjectStatus from, ProjectStatus to) =>
        Assert.True(ProjectStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(ProjectStatus.Draft, ProjectStatus.Approved)]
    [InlineData(ProjectStatus.Draft, ProjectStatus.Active)]
    [InlineData(ProjectStatus.Completed, ProjectStatus.Active)]
    [InlineData(ProjectStatus.Cancelled, ProjectStatus.Draft)]
    [InlineData(ProjectStatus.Approved, ProjectStatus.Draft)]
    public void DisallowedProjectTransitionsFailClosed(ProjectStatus from, ProjectStatus to)
    {
        Assert.False(ProjectStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidProjectTransitionException>(() => ProjectStateMachine.EnsureCanTransition(from, to));
    }

    [Fact]
    public void CannotApproveDirectlyFromDraft() =>
        Assert.Throws<InvalidProjectTransitionException>(() => NewProject().Approve(Now));

    [Fact]
    public void RenameAndChangeOwnerBlockedAfterApproval()
    {
        var project = NewProject();
        project.SubmitForAssessment(Now);
        project.Approve(Later);
        Assert.Throws<InvalidProjectTransitionException>(() => project.Rename(new ProjectName("X"), Later));
        Assert.Throws<InvalidProjectTransitionException>(() => project.ChangeOwner(new ProjectOwner("Y"), Later));
    }

    [Fact]
    public void RehydratePreservesState()
    {
        var id = new ProjectId(Guid.NewGuid());
        var tenant = new TenantId(Guid.NewGuid());
        var config = new ProjectConfiguration(new TargetTenant("contoso.onmicrosoft.com"), TargetArchivePolicy.PrimaryMailbox);
        var project = MigrationProject.Rehydrate(
            id, tenant, new ProjectName("P"), new ProjectOwner("O"), config,
            new ConfigurationVersion(3), config.ComputeHash(), ProjectStatus.Approved, Now, Later, new RowVersion(42));

        Assert.Equal(3, project.ConfigurationVersion.Value);
        Assert.Equal(ProjectStatus.Approved, project.Status);
        Assert.Equal(config.ComputeHash(), project.ConfigurationHash);
        Assert.Equal(42UL, project.RowVersion.Value);
    }
}
