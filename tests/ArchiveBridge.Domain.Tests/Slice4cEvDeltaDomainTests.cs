using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// Testes de domínio da fundação de delta strategy/freeze planning (Slice 4C, Passo 3, AB-4C-008): seleção
/// determinística de strategy (req 2/8), watermark opaco com lineage e rejeições fail-closed (req 3/4/13),
/// identidade canônica de execução (req 12), máquina de estados de freeze (req 9-11).
/// </summary>
public sealed class Slice4cEvDeltaDomainTests
{
    private static TenantId Tenant => new(Guid.NewGuid());

    private static ProjectId Project => new(Guid.NewGuid());

    private static readonly EvDeltaStrategyId StrategyV1 = new("EV-COMPOSITE-WATERMARK", 1);
    private static readonly EvDeltaStrategyId StrategyV2 = new("EV-COMPOSITE-WATERMARK", 2);

    // ---- EvDeltaStrategySelectionPolicy: versão/schema desconhecida bloqueia fail-closed (req 2) --------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9.9")]
    [InlineData("Unrecognized-Vendor-String")]
    public void UnknownOrUnrecognizedVersionSelectsNothing(string version)
    {
        var selection = EvDeltaStrategySelectionPolicy.Select(version, EvDeltaPhase.Baseline);

        Assert.Equal(EvDeltaStrategySelectionOutcome.Unknown, selection.Outcome);
        Assert.Null(selection.Selected);
    }

    [Theory]
    [InlineData("15.0.1")]
    [InlineData("14.2")]
    [InlineData("12.1.0")]
    [InlineData("10.0")]
    public void ACertifiedFamilyVersionSelectsTheDocumentedStrategyForEveryPhase(string version)
    {
        foreach (var phase in new[] { EvDeltaPhase.Baseline, EvDeltaPhase.Delta, EvDeltaPhase.FinalDelta })
        {
            var selection = EvDeltaStrategySelectionPolicy.Select(version, phase);

            Assert.Equal(EvDeltaStrategySelectionOutcome.Supported, selection.Outcome);
            Assert.Equal("EV-COMPOSITE-WATERMARK", selection.Selected!.StrategyId.Name);
            Assert.NotEqual(EvDeltaStrategyCertification.Certified, selection.Selected.Certification); // ADR-0013: nenhuma família começa Certified.
        }
    }

    [Fact]
    public void NoFamilyInTheEmbeddedMatrixStartsCertified()
    {
        var selection = EvDeltaStrategySelectionPolicy.Select("15.0", EvDeltaPhase.Baseline);
        Assert.True(selection.Selected!.Certification < EvDeltaStrategyCertification.Certified);
    }

    [Fact]
    public void TwoEligibleDescriptorsTiedAtTheHighestPrecedenceAreAmbiguousFailClosed()
    {
        var tied = new[]
        {
            new EvDeltaStrategyDescriptor(StrategyV1, "15.", EvDeltaStrategyCertification.Compatible, [EvDeltaPhase.Baseline], Precedence: 10),
            new EvDeltaStrategyDescriptor(StrategyV2, "15.", EvDeltaStrategyCertification.Compatible, [EvDeltaPhase.Baseline], Precedence: 10),
        };

        var selection = EvDeltaStrategySelectionPolicy.SelectFrom(tied, EvDeltaPhase.Baseline);

        Assert.Equal(EvDeltaStrategySelectionOutcome.Ambiguous, selection.Outcome);
        Assert.Null(selection.Selected);
    }

    [Fact]
    public void AHigherPrecedenceDescriptorWinsDeterministicallyOverALowerOne()
    {
        var candidates = new[]
        {
            new EvDeltaStrategyDescriptor(StrategyV1, "15.", EvDeltaStrategyCertification.Compatible, [EvDeltaPhase.Baseline], Precedence: 10),
            new EvDeltaStrategyDescriptor(StrategyV2, "15.", EvDeltaStrategyCertification.Compatible, [EvDeltaPhase.Baseline], Precedence: 20),
        };

        var selection = EvDeltaStrategySelectionPolicy.SelectFrom(candidates, EvDeltaPhase.Baseline);

        Assert.Equal(EvDeltaStrategySelectionOutcome.Supported, selection.Outcome);
        Assert.Equal(StrategyV2, selection.Selected!.StrategyId);
    }

    [Fact]
    public void ANotSupportedDescriptorIsNeverEligibleEvenIfItIsTheOnlyCandidate()
    {
        var vetoed = new[]
        {
            new EvDeltaStrategyDescriptor(StrategyV1, "99.", EvDeltaStrategyCertification.NotSupported, [EvDeltaPhase.Baseline], Precedence: 10),
        };

        var selection = EvDeltaStrategySelectionPolicy.SelectFrom(vetoed, EvDeltaPhase.Baseline);

        Assert.Equal(EvDeltaStrategySelectionOutcome.Unsupported, selection.Outcome);
    }

    [Fact]
    public void APhaseNotDeclaredAsSupportedByAnyDescriptorIsUnsupported()
    {
        var baselineOnly = new[]
        {
            new EvDeltaStrategyDescriptor(StrategyV1, "15.", EvDeltaStrategyCertification.Compatible, [EvDeltaPhase.Baseline], Precedence: 10),
        };

        var selection = EvDeltaStrategySelectionPolicy.SelectFrom(baselineOnly, EvDeltaPhase.FinalDelta);

        Assert.Equal(EvDeltaStrategySelectionOutcome.Unsupported, selection.Outcome);
    }

    // ---- EvWatermark: opaco, lineage, rejeições fail-closed (req 3/4/13) ---------------------------------

    private static EvWatermark IssueWatermark(
        TenantId tenant, ProjectId project, ConnectorId connector, string archiveId, EvDeltaPhase phase,
        EvDeltaStrategyId strategy, DateTimeOffset issuedAtUtc) =>
        EvWatermark.Issue(tenant, project, connector, archiveId, phase, strategy, Guid.NewGuid(), "opaque-token-1", issuedAtUtc);

    [Fact]
    public void ACrossScopeWatermarkNeverPrecedesADeltaForAnotherArchive()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var baseline = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1, now);

        var ex = Assert.Throws<EvWatermarkRejectedException>(
            () => baseline.EnsureCanPrecede(tenant, project, connector, "arch-2", StrategyV1));
        Assert.Equal(EvWatermarkRejectionReason.CrossScope, ex.Reason);
    }

    [Fact]
    public void AWatermarkFromAnotherTenantIsRejectedCrossScope()
    {
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var baseline = IssueWatermark(Tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1, now);

        var ex = Assert.Throws<EvWatermarkRejectedException>(
            () => baseline.EnsureCanPrecede(Tenant, project, connector, "arch-1", StrategyV1));
        Assert.Equal(EvWatermarkRejectionReason.CrossScope, ex.Reason);
    }

    [Fact]
    public void AWatermarkFromAnotherStrategyIsRejectedAsStrategyMismatch()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var baseline = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1, now);
        var otherStrategy = new EvDeltaStrategyId("OTHER-STRATEGY", 1);

        var ex = Assert.Throws<EvWatermarkRejectedException>(
            () => baseline.EnsureCanPrecede(tenant, project, connector, "arch-1", otherStrategy));
        Assert.Equal(EvWatermarkRejectionReason.StrategyMismatch, ex.Reason);
    }

    [Fact]
    public void AStrategyVersionDowngradeIsRejected()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var baseline = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV2, now);

        var ex = Assert.Throws<EvWatermarkRejectedException>(
            () => baseline.EnsureCanPrecede(tenant, project, connector, "arch-1", StrategyV1));
        Assert.Equal(EvWatermarkRejectionReason.StrategyDowngrade, ex.Reason);
    }

    [Fact]
    public void ASameOrHigherStrategyVersionIsAcceptedToPrecede()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var baseline = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1, now);

        baseline.EnsureCanPrecede(tenant, project, connector, "arch-1", StrategyV1); // não lança
        baseline.EnsureCanPrecede(tenant, project, connector, "arch-1", StrategyV2); // upgrade aceito, não lança
    }

    [Fact]
    public void AStaleCandidateWatermarkIsRejected()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var baseline = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1, now);
        var staleCandidate = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Delta, StrategyV1, now.AddSeconds(-1));

        var ex = Assert.Throws<EvWatermarkRejectedException>(() => baseline.EnsureSucceededBy(staleCandidate));
        Assert.Equal(EvWatermarkRejectionReason.Stale, ex.Reason);
    }

    [Fact]
    public void AWatermarkIssuedStrictlyAfterTheCurrentOneSucceedsIt()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var baseline = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1, now);
        var next = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Delta, StrategyV1, now.AddSeconds(1));

        baseline.EnsureSucceededBy(next); // não lança
    }

    [Fact]
    public void RehydrateFailsClosedWhenTheLineageHashDoesNotMatchTheLoadedFields()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var forgedHash = new Sha256Hash(DeterministicHash.Compute(["not-the-real-lineage"]).Value);

        var ex = Assert.Throws<EvWatermarkRejectedException>(() => EvWatermark.Rehydrate(
            WatermarkId.New(), tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1,
            Guid.NewGuid(), "token", now, forgedHash));
        Assert.Equal(EvWatermarkRejectionReason.Tampered, ex.Reason);
    }

    [Fact]
    public void RehydrateOfAnUntamperedWatermarkRoundTripsExactly()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var now = DateTimeOffset.UtcNow;
        var created = IssueWatermark(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1, now);

        var rehydrated = EvWatermark.Rehydrate(
            created.Id, tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, StrategyV1,
            created.ProducingExecutionId, created.OpaqueToken, now, created.LineageHash);

        Assert.Equal(created.LineageHash, rehydrated.LineageHash);
    }

    // ---- EvDeltaRunIdentity: identidade canônica, SEM strategy — mesma phase+watermark+archive converge (req 12) ----

    [Fact]
    public void IdenticalPhaseWatermarkArchiveProduceTheSameCanonicalIdentity()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var watermark = WatermarkId.New();

        var a = EvDeltaRunIdentity.Compute(tenant, project, connector, "arch-1", EvDeltaPhase.Delta, watermark);
        var b = EvDeltaRunIdentity.Compute(tenant, project, connector, "  arch-1  ", EvDeltaPhase.Delta, watermark);

        Assert.Equal(a.Value, b.Value);
        Assert.Equal(a.ToIdempotencyKey(), b.ToIdempotencyKey());
    }

    [Fact]
    public void ADifferentPreviousWatermarkProducesADifferentCanonicalIdentity()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();

        var a = EvDeltaRunIdentity.Compute(tenant, project, connector, "arch-1", EvDeltaPhase.Delta, WatermarkId.New());
        var b = EvDeltaRunIdentity.Compute(tenant, project, connector, "arch-1", EvDeltaPhase.Delta, WatermarkId.New());

        Assert.NotEqual(a.Value, b.Value);
    }

    [Fact]
    public void ADifferentPhaseProducesADifferentCanonicalIdentityEvenWithTheSameWatermark()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var watermark = WatermarkId.New();

        var delta = EvDeltaRunIdentity.Compute(tenant, project, connector, "arch-1", EvDeltaPhase.Delta, watermark);
        var finalDelta = EvDeltaRunIdentity.Compute(tenant, project, connector, "arch-1", EvDeltaPhase.FinalDelta, watermark);

        Assert.NotEqual(delta.Value, finalDelta.Value);
    }

    [Fact]
    public void BaselineHasNoPreviousWatermarkAndStillProducesAStableIdentity()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();

        var a = EvDeltaRunIdentity.Compute(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, previousWatermark: null);
        var b = EvDeltaRunIdentity.Compute(tenant, project, connector, "arch-1", EvDeltaPhase.Baseline, previousWatermark: null);

        Assert.Equal(a.Value, b.Value);
    }

    // ---- EvFreezeTransitions / EvFreezePlan: estado+autorização, nunca execução real (req 9-11) ----------

    [Theory]
    [InlineData(EvFreezeStatus.NotRequested, EvFreezeStatus.FreezeRequired, true)]
    [InlineData(EvFreezeStatus.FreezeRequired, EvFreezeStatus.FreezeAuthorized, true)]
    [InlineData(EvFreezeStatus.FreezeRequired, EvFreezeStatus.FreezeRejected, true)]
    [InlineData(EvFreezeStatus.FreezeRejected, EvFreezeStatus.FreezeRequired, true)]
    [InlineData(EvFreezeStatus.FreezeAuthorized, EvFreezeStatus.FinalDeltaReady, true)]
    [InlineData(EvFreezeStatus.FinalDeltaReady, EvFreezeStatus.RollbackRetentionRequired, true)]
    [InlineData(EvFreezeStatus.RollbackRetentionRequired, EvFreezeStatus.DecommissionBlocked, true)]
    [InlineData(EvFreezeStatus.DecommissionBlocked, EvFreezeStatus.FreezeRequired, false)]
    [InlineData(EvFreezeStatus.NotRequested, EvFreezeStatus.FreezeAuthorized, false)]
    [InlineData(EvFreezeStatus.FreezeRequired, EvFreezeStatus.FinalDeltaReady, false)]
    [InlineData(EvFreezeStatus.FreezeAuthorized, EvFreezeStatus.RollbackRetentionRequired, false)]
    public void TransitionsFollowTheExplicitAllowList(EvFreezeStatus from, EvFreezeStatus to, bool allowed)
    {
        Assert.Equal(allowed, EvFreezeTransitions.CanTransition(from, to));
    }

    [Fact]
    public void DecommissionBlockedHasNoOutgoingTransitionsWhatsoever()
    {
        foreach (var target in Enum.GetValues<EvFreezeStatus>())
        {
            Assert.False(EvFreezeTransitions.CanTransition(EvFreezeStatus.DecommissionBlocked, target));
        }
    }

    [Fact]
    public void AuthorizingWithUnspecifiedRoleIsAlwaysRejected()
    {
        var plan = EvFreezePlan.RequestFreeze(Tenant, Project, ConnectorId.New(), "arch-1");

        Assert.Throws<EvFreezeAuthorizationRequiredException>(() => plan.AuthorizeFreeze(
            "operator-1", EvFreezeAuthorizationRole.Unspecified, "justificativa", CorrelationId.New(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AuthorizingWithACompetentRolePersistsTheAuthorizationAndAdvancesTheState()
    {
        var plan = EvFreezePlan.RequestFreeze(Tenant, Project, ConnectorId.New(), "arch-1");
        var previousVersion = plan.Version;

        plan.AuthorizeFreeze("operator-1", EvFreezeAuthorizationRole.MigrationOperator, "janela aprovada", CorrelationId.New(), DateTimeOffset.UtcNow);

        Assert.Equal(EvFreezeStatus.FreezeAuthorized, plan.Status);
        Assert.NotNull(plan.Authorization);
        Assert.True(plan.Version > previousVersion);
    }

    [Fact]
    public void FinalDeltaReadyRequiresAPersistedAuthorization()
    {
        var plan = EvFreezePlan.RequestFreeze(Tenant, Project, ConnectorId.New(), "arch-1");

        // Nunca alcança FreezeAuthorized sem autorização — a transição em si já bloqueia (fail-closed).
        Assert.Throws<InvalidEvFreezeTransitionException>(plan.MarkFinalDeltaReady);
    }

    [Fact]
    public void DecommissionRemainsBlockedThroughTheFullHappyPath()
    {
        var plan = EvFreezePlan.RequestFreeze(Tenant, Project, ConnectorId.New(), "arch-1");
        plan.AuthorizeFreeze("operator-1", EvFreezeAuthorizationRole.TenantAdministrator, "janela aprovada", CorrelationId.New(), DateTimeOffset.UtcNow);
        plan.MarkFinalDeltaReady();
        plan.MarkRollbackRetentionRequired();
        plan.BlockDecommission();

        Assert.Equal(EvFreezeStatus.DecommissionBlocked, plan.Status);
        // Nenhuma transição de saída existe — provado acima por DecommissionBlockedHasNoOutgoingTransitionsWhatsoever.
    }

    [Fact]
    public void RejectingAFreezeClearsAnyPriorAuthorizationState()
    {
        var plan = EvFreezePlan.RequestFreeze(Tenant, Project, ConnectorId.New(), "arch-1");
        plan.RejectFreeze();

        Assert.Equal(EvFreezeStatus.FreezeRejected, plan.Status);
        Assert.Null(plan.Authorization);
    }
}
