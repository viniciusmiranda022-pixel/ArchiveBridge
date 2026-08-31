using ArchiveBridge.Application.ProductionReadiness;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
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

    // ---- AB-I8-002 blocker 1: capability matrix (ARCH.CAPABILITY_MATRIX_CURRENT) ----

    [Fact]
    public async Task CapabilityMatrixIsNotMeasuredWhenNoEvidenceWasEverRecorded()
    {
        var scope = NewScope();
        var store = new InMemoryCapabilityEvidenceStore();

        var result = await ReadinessGateEvidenceResolvers.ResolveCapabilityMatrixAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task CapabilityMatrixIsBlockedWhenTheCanonicalStatusIsUnknownEvenThoughEvidenceExists()
    {
        // CapabilityStatus.Unknown é o default fail-closed do tipo — a evidência EXISTE (não é NoEvidence),
        // mas o status documentado é Unknown; nunca promovido a Pass por omissão (AB-I8-001 escopo item 6).
        var scope = NewScope();
        var store = new InMemoryCapabilityEvidenceStore();
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), scope.Tenant, scope.Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            version: 1, CapabilityStatus.Unknown, sourceReference: null, documentationVersion: null, capabilityVersionLabel: null,
            Now, Correlation, Now);
        store.Seed(scope, evidence);

        var result = await ReadinessGateEvidenceResolvers.ResolveCapabilityMatrixAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Blocked, result.Status);
        Assert.NotEqual(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task CapabilityMatrixIsBlockedWhenEvidenceIsOlderThanTheFreshnessWindow()
    {
        var scope = NewScope();
        var store = new InMemoryCapabilityEvidenceStore();
        var recordedLongAgo = Now - CapabilityEvidencePolicy.DefaultMaxAge - TimeSpan.FromDays(1);
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), scope.Tenant, scope.Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            version: 1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, recordedLongAgo, Correlation, recordedLongAgo);
        store.Seed(scope, evidence);

        var result = await ReadinessGateEvidenceResolvers.ResolveCapabilityMatrixAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
        Assert.Equal("CAPABILITY_EVIDENCE_STALE", result.ReasonCode);
    }

    [Fact]
    public async Task CapabilityMatrixPassesWhenEveryKnownRouteIsGeneralAvailabilityAndFresh()
    {
        var scope = NewScope();
        var store = new InMemoryCapabilityEvidenceStore();
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), scope.Tenant, scope.Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            version: 1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, Now, Correlation, Now);
        store.Seed(scope, evidence);

        var result = await ReadinessGateEvidenceResolvers.ResolveCapabilityMatrixAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    // ---- AB-I8-002 blocker 2: M365.TENANT_PRECHECK ----

    [Fact]
    public async Task TenantPrecheckIsNotMeasuredWhenNoMailboxWasEverPrechecked()
    {
        var scope = NewScope();
        var store = new InMemoryMailboxPrecheckStore();

        var result = await ReadinessGateEvidenceResolvers.ResolveTenantPrecheckAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task TenantPrecheckIsBlockedWhenTheMostRecentPrecheckIsNotActive()
    {
        var scope = NewScope();
        var store = new InMemoryMailboxPrecheckStore();
        var snapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), scope.Tenant, scope.Project, new ArchiveRef("mailbox-a@tenant.example", TargetArchiveId.FromMailbox("mailbox-a@tenant.example")),
            version: 1, exchangeGuid: null, archiveGuid: null, MailboxArchiveStatus.Disabled, recipientTypeDetails: null,
            autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false,
            archiveItemCount: null, archiveTotalSizeBytes: null, observedAvailableBytes: null, Now, Correlation, Now);
        store.Seed(scope, snapshot);

        var result = await ReadinessGateEvidenceResolvers.ResolveTenantPrecheckAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Blocked, result.Status);
        Assert.NotEqual(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task TenantPrecheckPassesWhenTheMostRecentPrecheckIsActive()
    {
        var scope = NewScope();
        var store = new InMemoryMailboxPrecheckStore();
        var snapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), scope.Tenant, scope.Project, new ArchiveRef("mailbox-a@tenant.example", TargetArchiveId.FromMailbox("mailbox-a@tenant.example")),
            version: 1, exchangeGuid: Guid.NewGuid(), archiveGuid: Guid.NewGuid(), MailboxArchiveStatus.Active, "UserMailbox",
            autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false,
            archiveItemCount: 10, archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000, Now, Correlation, Now);
        store.Seed(scope, snapshot);

        var result = await ReadinessGateEvidenceResolvers.ResolveTenantPrecheckAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    // ---- AB-I8-002 blocker 2: M365.MAPPING_VALIDATOR ----

    [Fact]
    public async Task MappingValidatorIsNotMeasuredWhenNoAttemptWasEverRecorded()
    {
        var scope = NewScope();
        var store = new InMemoryMappingValidationStore();

        var result = await ReadinessGateEvidenceResolvers.ResolveMappingValidatorAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task MappingValidatorFailsWhenTheMostRecentAttemptIsInvalid()
    {
        var scope = NewScope();
        var store = new InMemoryMappingValidationStore();
        store.Seed(MappingAttempt(scope, MappingValidationAttemptOutcome.Invalid));

        var result = await ReadinessGateEvidenceResolvers.ResolveMappingValidatorAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Fail, result.Status);
        Assert.NotEqual(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task MappingValidatorIsBlockedWhenTheMostRecentAttemptIsRejected()
    {
        var scope = NewScope();
        var store = new InMemoryMappingValidationStore();
        store.Seed(MappingAttempt(scope, MappingValidationAttemptOutcome.Rejected));

        var result = await ReadinessGateEvidenceResolvers.ResolveMappingValidatorAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Blocked, result.Status);
        Assert.NotEqual(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task MappingValidatorPassesWhenTheMostRecentAttemptIsValid()
    {
        var scope = NewScope();
        var store = new InMemoryMappingValidationStore();
        store.Seed(MappingAttempt(scope, MappingValidationAttemptOutcome.Valid));

        var result = await ReadinessGateEvidenceResolvers.ResolveMappingValidatorAsync(store, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    private static MappingValidationAttempt MappingAttempt(TenantScope scope, MappingValidationAttemptOutcome outcome) =>
        new(
            Guid.NewGuid(), scope, WaveId.New(), WaveVersion: 1, SomeFingerprint, SomeFingerprint,
            MappingSchemaVersion: 1, MappingPolicyVersion: 1, new ContentCodePage(65001), SomeFingerprint, SizeBytes: 128,
            RowCount: 10, outcome, IssueCount: outcome == MappingValidationAttemptOutcome.Valid ? 0 : 1, IssuesTruncated: false,
            DisplayFileName: "mapping.csv", UserId: Guid.NewGuid(), RequestedBy: "alice", Correlation, IdempotencyKey: Guid.NewGuid(),
            Now, Issues: []);

    // ---- AB-I8-002 blocker 2: M365.AZCOPY_VERSION_HOMOLOGATED ----

    private static readonly AzCopyBinaryIdentity HomologatedBinary = new("10.25.0", new Sha256Hash(new string('b', 64)));
    private static readonly AzCopyHomologationCatalog HomologatedCatalog = new([HomologatedBinary]);

    [Fact]
    public async Task AzCopyHomologationIsNotMeasuredWhenNoAttemptWasEverUploaded()
    {
        var scope = NewScope();
        var store = new InMemoryPurviewUploadAttemptStore();

        var result = await ReadinessGateEvidenceResolvers.ResolveAzCopyHomologationAsync(
            store, HomologatedCatalog, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status);
    }

    [Fact]
    public async Task AzCopyHomologationIsBlockedWhenTheObservedBinaryDoesNotMatchTheCatalog()
    {
        var scope = NewScope();
        var store = new InMemoryPurviewUploadAttemptStore();
        var driftedBinary = new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('c', 64)));
        store.Seed(scope, UploadedAttempt(scope, driftedBinary));

        var result = await ReadinessGateEvidenceResolvers.ResolveAzCopyHomologationAsync(
            store, HomologatedCatalog, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Blocked, result.Status);
        Assert.Equal("AZCOPY_BINARY_NOT_HOMOLOGATED", result.ReasonCode);
    }

    [Fact]
    public async Task AzCopyHomologationPassesWhenTheObservedBinaryMatchesTheCatalogExactly()
    {
        var scope = NewScope();
        var store = new InMemoryPurviewUploadAttemptStore();
        store.Seed(scope, UploadedAttempt(scope, HomologatedBinary));

        var result = await ReadinessGateEvidenceResolvers.ResolveAzCopyHomologationAsync(
            store, HomologatedCatalog, scope, Now, CancellationToken.None);

        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    private static PurviewUploadAttemptRecord UploadedAttempt(TenantScope scope, AzCopyBinaryIdentity binary)
    {
        var execution = PartitionExecutionId.New();
        var manifest = new[] { new PurviewUploadFileManifestItem(execution, PurviewRemotePstName.ForPart(ArtifactId.New(), 1), SomeFingerprint, 4096) };
        var prefix = PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, WaveId.New());
        var evidence = new PurviewUploadEvidence(binary, prefix, manifest);
        return new PurviewUploadAttemptRecord(
            PurviewUploadRequestId.New(), PurviewUploadAttemptId.New(), AttemptNumber: 1, SomeFingerprint,
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, Now, Now);
    }

    // ---- AB-I8-002 blocker 1: policy version fingerprint (nunca aceito do caller) ----

    [Fact]
    public async Task PolicyVersionFingerprintIsDeterministicForTheSameCanonicalEvidence()
    {
        var scope = NewScope();
        var wdacStore = new InMemoryWdacPolicyEvidenceStore();
        var entry = WdacAllowlistEntry.Create(publisher: null, sha256: SomeFingerprint, pathRule: null);
        var policy = WdacPolicyEvidence.Record(scope.Tenant, scope.Project, policyVersion: 1, [entry], "svc-hardening", "ServiceAccount", Correlation, Now);
        wdacStore.Seed(scope, policy);
        var invariants = ProductionReadinessPolicyInvariants.Evaluate(Now);

        var first = await ReadinessGateEvidenceResolvers.ResolvePolicyVersionFingerprintAsync(wdacStore, scope, invariants, CancellationToken.None);
        var second = await ReadinessGateEvidenceResolvers.ResolvePolicyVersionFingerprintAsync(wdacStore, scope, invariants, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task PolicyVersionFingerprintChangesWhenTheWdacPolicyChanges()
    {
        var scope = NewScope();
        var wdacStore = new InMemoryWdacPolicyEvidenceStore();
        var invariants = ProductionReadinessPolicyInvariants.Evaluate(Now);
        var before = await ReadinessGateEvidenceResolvers.ResolvePolicyVersionFingerprintAsync(wdacStore, scope, invariants, CancellationToken.None);

        var entry = WdacAllowlistEntry.Create(publisher: null, sha256: SomeFingerprint, pathRule: null);
        var policy = WdacPolicyEvidence.Record(scope.Tenant, scope.Project, policyVersion: 1, [entry], "svc-hardening", "ServiceAccount", Correlation, Now);
        wdacStore.Seed(scope, policy);
        var after = await ReadinessGateEvidenceResolvers.ResolvePolicyVersionFingerprintAsync(wdacStore, scope, invariants, CancellationToken.None);

        Assert.NotEqual(before, after);
    }
}
