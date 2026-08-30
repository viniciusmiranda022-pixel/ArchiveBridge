using ArchiveBridge.Application.ProductionReadiness;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.Security;
using Xunit;

namespace ArchiveBridge.Application.Tests.ProductionReadiness;

/// <summary>
/// AB-I8-001 §10 — cenários explícitos do work order cobertos ao nível dos resolvers puros de evidência
/// (sem SQL): pen-test ausente NUNCA vira Pass, RTO/RPO não medidos permanecem NotMeasured, e build digest
/// alterado invalida SBOM/signatures.
/// </summary>
public sealed class ReadinessGateEvidenceResolversTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    [Fact]
    public async Task PenTestNeverPassesWhenNoBundleWasEverPrepared()
    {
        var scope = NewScope();
        var store = new InMemoryPenTestReadinessStore();

        var result = await ReadinessGateEvidenceResolvers.ResolvePenTestAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task PenTestNeverPassesEvenWhenABlockedBundleExists()
    {
        // PenTestReadinessStatus NÃO POSSUI um caso Pass — este teste prova que o resolver nunca inventa um
        // status inexistente no tipo de origem; o pior que pode acontecer é Blocked.
        var scope = NewScope();
        var store = new InMemoryPenTestReadinessStore();
        var bundle = PenTestReadinessBundle.Blocked(
            scope.Tenant, scope.Project, bundleVersion: 1, "scope", "attack-surface", "trust-boundaries", "fixtures",
            "known-blocked", SomeFingerprint, "no independent tester contracted yet", "svc-security", "ServiceAccount",
            Correlation, Now);
        store.Seed(scope, bundle);

        var result = await ReadinessGateEvidenceResolvers.ResolvePenTestAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Blocked, result.Status);
        Assert.NotEqual(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task RtoIsNotMeasuredWhenNoRestoreDrillWasEverExecuted()
    {
        var scope = NewScope();
        var store = new InMemoryRecoveryReadinessStore();

        var result = await ReadinessGateEvidenceResolvers.ResolveRtoAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task RpoIsStructurallyIncapableOfPassingEvenWhenADrillIsRecorded()
    {
        // RecoveryReadinessRecord.Pass() lança para ControlPlaneRpo/EvidenceLogicalRpo nesta baseline
        // (AB-I7-007 item 2) — só é possível seedar um registro Blocked/NotMeasured para RPO.
        var scope = NewScope();
        var store = new InMemoryRecoveryReadinessStore();
        var blocked = RecoveryReadinessRecord.Blocked(
            scope.Tenant, scope.Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRpo,
            objectiveThreshold: null, measurement: null, SomeFingerprint, failureDomain: "no failure-boundary drill exists yet",
            notes: string.Empty, executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now);
        store.Seed(scope, blocked);

        var result = await ReadinessGateEvidenceResolvers.ResolveRpoAsync(store, scope, Now, CancellationToken.None);

        Assert.NotEqual(ReadinessControlStatus.Pass, result.Status);
        Assert.Equal(ReadinessControlStatus.Blocked, result.Status);
    }

    [Fact]
    public async Task RtoWithAGenuinePassingDrillResolvesToPass()
    {
        var scope = NewScope();
        var store = new InMemoryRecoveryReadinessStore();
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromHours(1));
        var passed = RecoveryReadinessRecord.Pass(
            scope.Tenant, scope.Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            objectiveThreshold: TimeSpan.FromHours(4), measurement, SomeFingerprint, notes: "restore drill ok.",
            executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now);
        store.Seed(scope, passed);

        var result = await ReadinessGateEvidenceResolvers.ResolveRtoAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task SbomAndSignaturesFailWhenTheApprovedBuildDigestDiffersFromTheReviewedBuild()
    {
        var scope = NewScope();
        var store = new InMemoryBuildProvenanceStore();
        var approved = BuildProvenanceRecord.Approve(
            scope.Tenant, scope.Project, "ArchiveBridge.ControlPlane", artifactVersion: 1,
            sourceCommitSha: "0123456789abcdef0123456789abcdef01234567", builderIdentity: "ci-runner", Now,
            artifactDigest: new Sha256Hash(new string('b', 64)), approvedBy: "svc-supply-chain", approvedByRole: "ServiceAccount",
            Correlation, Now);
        store.Seed(scope, approved);

        // O digest sob revisão diverge do digest realmente aprovado — drift.
        var result = await ReadinessGateEvidenceResolvers.ResolveSbomAndSignaturesAsync(
            store, scope, "ArchiveBridge.ControlPlane", "0123456789abcdef0123456789abcdef01234567",
            new Sha256Hash(new string('c', 64)), Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Fail, result.Status);
        Assert.Equal("BUILD_PROVENANCE_DRIFT_FROM_REVIEWED_BUILD", result.ReasonCode);
    }

    [Fact]
    public async Task SbomAndSignaturesPassWhenTheApprovedBuildMatchesTheReviewedBuildExactly()
    {
        var scope = NewScope();
        var store = new InMemoryBuildProvenanceStore();
        var digest = new Sha256Hash(new string('b', 64));
        const string commitSha = "0123456789abcdef0123456789abcdef01234567";
        var approved = BuildProvenanceRecord.Approve(
            scope.Tenant, scope.Project, "ArchiveBridge.ControlPlane", artifactVersion: 1, commitSha, "ci-runner", Now, digest,
            "svc-supply-chain", "ServiceAccount", Correlation, Now);
        store.Seed(scope, approved);

        var result = await ReadinessGateEvidenceResolvers.ResolveSbomAndSignaturesAsync(
            store, scope, "ArchiveBridge.ControlPlane", commitSha, digest, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task SbomAndSignaturesAreNotMeasuredWhenNoBuildWasEverApproved()
    {
        var scope = NewScope();
        var store = new InMemoryBuildProvenanceStore();

        var result = await ReadinessGateEvidenceResolvers.ResolveSbomAndSignaturesAsync(
            store, scope, "ArchiveBridge.ControlPlane", "0123456789abcdef0123456789abcdef01234567", SomeFingerprint, Now,
            CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task WdacDefenderPatchingIsNotMeasuredWhenNothingWasEverVerified()
    {
        var scope = NewScope();
        var hardeningStore = new InMemoryWorkerHardeningBaselineStore();
        var wdacStore = new InMemoryWdacPolicyEvidenceStore();

        var result = await ReadinessGateEvidenceResolvers.ResolveWdacDefenderPatchingAsync(
            hardeningStore, wdacStore, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task WdacDefenderPatchingPassesOnlyWhenAllRequiredControlsPassAndAPolicyExists()
    {
        var scope = NewScope();
        var hardeningStore = new InMemoryWorkerHardeningBaselineStore();
        var wdacStore = new InMemoryWdacPolicyEvidenceStore();
        var measurement = new WorkerHardeningMeasurement(Now, "local policy query");

        foreach (var control in WorkerHardeningBaselineCatalog.AllControls)
        {
            if (WorkerHardeningBaselineCatalog.Applicability(control) != WorkerHardeningApplicability.Required)
            {
                continue;
            }

            var record = WorkerHardeningControlRecord.Pass(
                scope.Tenant, scope.Project, control, controlVersion: 1, measurement, SomeFingerprint, notes: string.Empty,
                executedBy: "svc-hardening", executedByRole: "ServiceAccount", Correlation, Now);
            hardeningStore.Seed(scope, record);
        }

        var entry = WdacAllowlistEntry.Create(publisher: null, sha256: SomeFingerprint, pathRule: null);
        var policy = WdacPolicyEvidence.Record(
            scope.Tenant, scope.Project, policyVersion: 1, [entry], "svc-hardening", "ServiceAccount", Correlation, Now);
        wdacStore.Seed(scope, policy);

        var result = await ReadinessGateEvidenceResolvers.ResolveWdacDefenderPatchingAsync(
            hardeningStore, wdacStore, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task IncidentResponseIsNotMeasuredWhenOnlyTwoOfThreeDrillsWereExercised()
    {
        var scope = NewScope();
        var store = new InMemoryIncidentResponseDrillStore();
        var record = IncidentResponseDrillRecord.Record(
            scope.Tenant, scope.Project, IncidentResponseDrillType.SecretLeakCanary, drillVersion: 1,
            IncidentResponseDrillOutcome.Contained, Now, Now + TimeSpan.FromMinutes(5), SomeFingerprint,
            disposition: "secret redacted as expected", executedBy: "svc-security", executedByRole: "ServiceAccount",
            Correlation, Now);
        store.Seed(scope, record);

        var result = await ReadinessGateEvidenceResolvers.ResolveIncidentResponseAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }
}
