using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// Slice 4B, Passo 2 — regras de PLANEJAMENTO de particionamento no Domain: política documentada (nunca
/// inventada), identidade determinística, fail-closed sobre inspeção canônica/hash/diagnóstico, e
/// representação HONESTA da incapacidade de derivar boundaries quando falta inventário de itens.
/// </summary>
public sealed class Slice4bPartitionPlanningDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly PartitionPlannerIdentity Planner = new("TestPlanner", "1.0");

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static MigrationArtifact Custody(Sha256Hash hash, long size = 4096) =>
        MigrationArtifact.Register(
            new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()),
            new PstRelativePath("mailboxes/alice.pst"), hash, size, Now);

    private static PstInspectionRecord Canonical(
        MigrationArtifact custody,
        long observedSize,
        PstStructuralDiagnostic diagnostic = PstStructuralDiagnostic.Valid) =>
        PstInspectionRecord.Complete(
            InspectionId.New(), custody.Tenant, custody.Project, custody.Id, custody.RegisteredHash,
            custody.RegisteredHash, observedSize, diagnostic, PstFormatVariant.Unicode2013Plus,
            "HeaderOnlyPstInspectionEngine", "1.0", CorrelationId.New(), Now, Now);

    private static PartitionPlan Plan(
        MigrationArtifact custody, PstInspectionRecord? inspection, PartitionPolicy? policy = null) =>
        PartitionPlanning.Plan(
            custody, inspection, policy ?? PartitionPolicy.RunbookDefault, Planner, CorrelationId.New(), Now);

    // ---- Política: valores DOCUMENTADOS no runbook §20.1, nunca escolhidos aqui ----

    [Fact]
    public void RunbookDefaultPolicyMatchesTheDocumentedLimits()
    {
        Assert.Equal(18L * 1024 * 1024 * 1024, PartitionPolicy.RunbookDefault.TargetPartBytes); // 18 GiB
        Assert.Equal(20_000_000_000L, PartitionPolicy.RunbookDefault.HardPartBytes); // 20 GB
        Assert.True(PartitionPolicy.RunbookDefault.TargetPartBytes <= PartitionPolicy.RunbookDefault.HardPartBytes);
    }

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(-1L, 100L)]
    [InlineData(100L, 0L)]
    [InlineData(100L, -1L)]
    public void PolicyRejectsNonPositiveLimits(long target, long hard) =>
        Assert.ThrowsAny<ArgumentException>(() => PartitionPolicy.Create(target, hard));

    [Fact]
    public void PolicyRejectsTargetAboveHardLimit() =>
        Assert.Throws<ArgumentException>(() => PartitionPolicy.Create(200, 100));

    [Fact]
    public void PolicyCanonicalJsonIsStableAndOrdered() =>
        Assert.Equal(
            "{\"hardPartBytes\":20000000000,\"targetPartBytes\":19327352832}",
            PartitionPolicy.RunbookDefault.CanonicalJson);

    [Fact]
    public void PolicyFingerprintIsDeterministicAndSensitiveToEveryLimit()
    {
        var baseline = PartitionPolicy.Create(100, 200);
        Assert.Equal(baseline.Fingerprint, PartitionPolicy.Create(100, 200).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, PartitionPolicy.Create(101, 200).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, PartitionPolicy.Create(100, 201).Fingerprint);
    }

    // ---- Identidade determinística do plano (runbook §20.2 + work order §4) ----

    [Fact]
    public void PlanHashIsStableForTheSameInputsAndIndependentOfTheServerGeneratedIds()
    {
        var custody = Custody(Hash("stable"));
        var inspection = Canonical(custody, 1024);

        var first = Plan(custody, inspection);
        var second = Plan(custody, inspection);

        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(first.Reason, second.Reason);
        Assert.NotEqual(first.Id, second.Id); // identidade da LINHA é nova; identidade do PLANO é a mesma.
    }

    [Fact]
    public void PlanHashChangesWithEveryIdentityComponent()
    {
        var custody = Custody(Hash("baseline"));
        var inspection = Canonical(custody, 1024);
        var source = new PartitionPlanSource(
            custody.Id, inspection.Id, custody.RegisteredHash, 1024, "Engine", "1.0");
        var policy = PartitionPolicy.RunbookDefault;

        var baseline = PartitionPlanIdentity.ComputePlanHash(custody.Tenant, custody.Project, source, policy, Planner);

        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(
            new TenantId(Guid.NewGuid()), custody.Project, source, policy, Planner));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(
            custody.Tenant, new ProjectId(Guid.NewGuid()), source, policy, Planner));
        Assert.Equal(baseline, PartitionPlanIdentity.ComputePlanHash(
            custody.Tenant, custody.Project, source, policy, Planner)); // determinístico para a mesma entrada

        var otherArtifact = new PartitionPlanSource(ArtifactId.New(), inspection.Id, custody.RegisteredHash, 1024, "Engine", "1.0");
        var otherInspection = new PartitionPlanSource(custody.Id, InspectionId.New(), custody.RegisteredHash, 1024, "Engine", "1.0");
        var otherHash = new PartitionPlanSource(custody.Id, inspection.Id, Hash("drifted"), 1024, "Engine", "1.0");
        var otherEngineName = new PartitionPlanSource(custody.Id, inspection.Id, custody.RegisteredHash, 1024, "OtherEngine", "1.0");
        var otherEngineVersion = new PartitionPlanSource(custody.Id, inspection.Id, custody.RegisteredHash, 1024, "Engine", "2.0");

        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(custody.Tenant, custody.Project, otherArtifact, policy, Planner));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(custody.Tenant, custody.Project, otherInspection, policy, Planner));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(custody.Tenant, custody.Project, otherHash, policy, Planner));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(custody.Tenant, custody.Project, otherEngineName, policy, Planner));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(custody.Tenant, custody.Project, otherEngineVersion, policy, Planner));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(
            custody.Tenant, custody.Project, source, PartitionPolicy.Create(1024, 2048), Planner));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(
            custody.Tenant, custody.Project, source, policy, new PartitionPlannerIdentity("TestPlanner", "2.0")));
        Assert.NotEqual(baseline, PartitionPlanIdentity.ComputePlanHash(
            custody.Tenant, custody.Project, source, policy, new PartitionPlannerIdentity("OtherPlanner", "1.0")));
    }

    [Fact]
    public void PartKeyIsOpaqueDerivedFromThePlanHashAndUniquePerSequence()
    {
        var planHash = Hash("plan");
        var first = PartitionPlanIdentity.ComputePartKey(planHash, 1);
        var second = PartitionPlanIdentity.ComputePartKey(planHash, 2);

        Assert.Equal(first, PartitionPlanIdentity.ComputePartKey(planHash, 1)); // determinístico
        Assert.NotEqual(first, second);
        Assert.Equal(64, first.Value.Length);
        Assert.All(first.Value, character => Assert.True(char.IsAsciiDigit(character) || (character >= 'a' && character <= 'f')));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartitionPlanIdentity.ComputePartKey(planHash, 0));
    }

    [Fact]
    public void PartKeyNeverCarriesThePathOrAnyMailboxIdentifier()
    {
        var custody = Custody(Hash("opaque"));
        var plan = Plan(custody, Canonical(custody, 1024));

        var part = Assert.Single(plan.Parts);
        Assert.DoesNotContain("alice", part.PartKey.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pst", part.PartKey.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mailboxes", part.PartKey.Value, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Decisão de planejamento ----

    [Fact]
    public void ArtifactWithinTheTargetIsPlannedAsASinglePartCoveringTheWholeSource()
    {
        var custody = Custody(Hash("small"));
        var plan = Plan(custody, Canonical(custody, 4096));

        Assert.Equal(PartitionPlanOutcome.Planned, plan.Outcome);
        Assert.Equal(PartitionPlanReason.SinglePartWithinTarget, plan.Reason);
        Assert.True(plan.IsCanonical);

        var part = Assert.Single(plan.Parts);
        Assert.Equal(1, part.Sequence);
        Assert.Equal(4096, part.PlannedSizeBytes);
        Assert.True(part.CoversEntireSource);
    }

    [Fact]
    public void ArtifactExactlyAtTheTargetStillFitsInOnePart()
    {
        var policy = PartitionPolicy.Create(4096, 8192);
        var custody = Custody(Hash("exact"));
        var plan = Plan(custody, Canonical(custody, 4096), policy);

        Assert.Equal(PartitionPlanReason.SinglePartWithinTarget, plan.Reason);
        Assert.Equal(4096, Assert.Single(plan.Parts).PlannedSizeBytes);
    }

    [Fact]
    public void ArtifactAboveTheTargetIsUnsupportedWithNoPartsAndNoInventedCounts()
    {
        var policy = PartitionPolicy.Create(4096, 8192);
        var custody = Custody(Hash("big"));
        var plan = Plan(custody, Canonical(custody, 4097), policy);

        Assert.Equal(PartitionPlanOutcome.Unsupported, plan.Outcome);
        Assert.Equal(PartitionPlanReason.ItemInventoryUnavailable, plan.Reason);
        Assert.False(plan.IsCanonical);
        Assert.Empty(plan.Parts); // nenhum boundary/contagem/offset é inventado quando falta inventário.
        Assert.Equal(4097, plan.Source.SourceSizeBytes); // o que FOI observado continua registrado, honestamente.
    }

    [Fact]
    public void MissingCanonicalInspectionIsBlockedWithoutAnyInspectionIdentity()
    {
        var custody = Custody(Hash("uninspected"));
        var plan = Plan(custody, inspection: null);

        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome);
        Assert.Equal(PartitionPlanReason.CanonicalInspectionUnavailable, plan.Reason);
        Assert.Null(plan.Source.Inspection);
        Assert.Null(plan.Source.SourceSizeBytes);
        Assert.Empty(plan.Parts);
        Assert.False(plan.IsCanonical);
    }

    [Fact]
    public void StaleInspectionIsBlockedAsSourceHashDivergence()
    {
        var custody = Custody(Hash("registered"));
        var stale = PstInspectionRecord.Stale(
            InspectionId.New(), custody.Tenant, custody.Project, custody.Id, custody.RegisteredHash,
            Hash("drifted"), 4096, "Engine", "1.0", CorrelationId.New(), Now, Now);

        var plan = Plan(custody, stale);

        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome);
        Assert.Equal(PartitionPlanReason.SourceHashDivergence, plan.Reason);
        Assert.Empty(plan.Parts);
    }

    [Fact]
    public void InspectionOfADifferentHashThanTheRegisteredCustodyIsBlocked()
    {
        var custody = Custody(Hash("current"));
        var otherHash = Hash("previous");
        var inspectionOfAnotherHash = PstInspectionRecord.Complete(
            InspectionId.New(), custody.Tenant, custody.Project, custody.Id, otherHash, otherHash, 4096,
            PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, "Engine", "1.0",
            CorrelationId.New(), Now, Now);

        var plan = Plan(custody, inspectionOfAnotherHash);

        // A inspeção É canônica para o SEU hash, mas esse hash não é o registrado em custódia agora:
        // fail-closed, jamais planeja sobre uma inspeção obsoleta.
        Assert.True(inspectionOfAnotherHash.IsCanonical);
        Assert.Equal(PartitionPlanReason.SourceHashDivergence, plan.Reason);
    }

    [Theory]
    [InlineData(PstStructuralDiagnostic.TooSmall)]
    [InlineData(PstStructuralDiagnostic.InvalidSignature)]
    [InlineData(PstStructuralDiagnostic.InvalidClientSignature)]
    [InlineData(PstStructuralDiagnostic.UnsupportedVersion)]
    public void StructurallyInvalidPstIsBlockedNeverPlanned(PstStructuralDiagnostic diagnostic)
    {
        var custody = Custody(Hash("invalid"));
        var plan = Plan(custody, Canonical(custody, 4096, diagnostic));

        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome);
        Assert.Equal(PartitionPlanReason.StructuralDiagnosticNotValid, plan.Reason);
        Assert.Empty(plan.Parts);
    }

    [Fact]
    public void ReadErrorInspectionIsBlockedBecauseItIsNeverCanonical()
    {
        var custody = Custody(Hash("unreadable"));
        var readError = PstInspectionRecord.Complete(
            InspectionId.New(), custody.Tenant, custody.Project, custody.Id, custody.RegisteredHash,
            observedHash: null, observedSizeBytes: null, PstStructuralDiagnostic.ReadError,
            PstFormatVariant.Unknown, "Engine", "1.0", CorrelationId.New(), Now, Now);

        var plan = Plan(custody, readError);

        Assert.False(readError.IsCanonical);
        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome);
        Assert.Empty(plan.Parts);
    }

    [Fact]
    public void ChangingThePolicyChangesThePlanIdentitySoNoStalePlanIsEverReused()
    {
        var custody = Custody(Hash("policy"));
        var inspection = Canonical(custody, 4096);

        var underDefault = Plan(custody, inspection);
        var underOtherPolicy = Plan(custody, inspection, PartitionPolicy.Create(1_000_000, 2_000_000));

        Assert.NotEqual(underDefault.PlanHash, underOtherPolicy.PlanHash);
        Assert.NotEqual(
            Assert.Single(underDefault.Parts).PartKey,
            Assert.Single(underOtherPolicy.Parts).PartKey);
    }

    // ---- Invariantes estruturais do agregado ----

    [Fact]
    public void OnlyAPlannedOutcomeWithPartsIsCanonical()
    {
        var custody = Custody(Hash("canonicity"));
        Assert.True(Plan(custody, Canonical(custody, 4096)).IsCanonical);
        Assert.False(Plan(custody, inspection: null).IsCanonical);
        Assert.False(Plan(custody, Canonical(custody, 4097), PartitionPolicy.Create(4096, 8192)).IsCanonical);
    }

    [Fact]
    public void ANonPlannedOutcomeCanNeverCarryParts()
    {
        var custody = Custody(Hash("no-parts"));
        var source = new PartitionPlanSource(custody.Id, null, custody.RegisteredHash, null, null, null);
        var part = new PartitionPlanPart(PartitionPlanPartId.New(), 1, Hash("k"), 10, true);

        Assert.Throws<ArgumentException>(() => PartitionPlan.Create(
            PartitionPlanId.New(), custody.Tenant, custody.Project, source, PartitionPolicy.RunbookDefault,
            Planner, Hash("plan"), PartitionPlanReason.CanonicalInspectionUnavailable, [part],
            CorrelationId.New(), Now));
    }

    [Fact]
    public void PlannedPartsMustCoverExactlyTheObservedSourceSize()
    {
        var custody = Custody(Hash("coverage"));
        var source = new PartitionPlanSource(custody.Id, InspectionId.New(), custody.RegisteredHash, 100, "Engine", "1.0");
        var tooSmall = new PartitionPlanPart(PartitionPlanPartId.New(), 1, Hash("k"), 99, true);

        Assert.Throws<ArgumentException>(() => PartitionPlan.Create(
            PartitionPlanId.New(), custody.Tenant, custody.Project, source, PartitionPolicy.RunbookDefault,
            Planner, Hash("plan"), PartitionPlanReason.SinglePartWithinTarget, [tooSmall], CorrelationId.New(), Now));
    }

    [Fact]
    public void NoPlannedPartMayExceedTheHardLimit()
    {
        var custody = Custody(Hash("hard-limit"));
        var source = new PartitionPlanSource(custody.Id, InspectionId.New(), custody.RegisteredHash, 300, "Engine", "1.0");
        var oversized = new PartitionPlanPart(PartitionPlanPartId.New(), 1, Hash("k"), 300, true);

        Assert.Throws<ArgumentException>(() => PartitionPlan.Create(
            PartitionPlanId.New(), custody.Tenant, custody.Project, source, PartitionPolicy.Create(100, 200),
            Planner, Hash("plan"), PartitionPlanReason.SinglePartWithinTarget, [oversized], CorrelationId.New(), Now));
    }

    [Fact]
    public void PlannedPartSequencesMustBeContiguousFromOne()
    {
        var custody = Custody(Hash("sequence"));
        var source = new PartitionPlanSource(custody.Id, InspectionId.New(), custody.RegisteredHash, 20, "Engine", "1.0");
        var second = new PartitionPlanPart(PartitionPlanPartId.New(), 2, Hash("k"), 20, false);

        Assert.Throws<ArgumentException>(() => PartitionPlan.Create(
            PartitionPlanId.New(), custody.Tenant, custody.Project, source, PartitionPolicy.RunbookDefault,
            Planner, Hash("plan"), PartitionPlanReason.SinglePartWithinTarget, [second], CorrelationId.New(), Now));
    }

    [Fact]
    public void APlannedOutcomeRequiresTheCanonicalInspectionIdentityAndTheEngine()
    {
        var custody = Custody(Hash("identity"));
        var withoutInspection = new PartitionPlanSource(custody.Id, null, custody.RegisteredHash, 10, "Engine", "1.0");
        var withoutEngine = new PartitionPlanSource(custody.Id, InspectionId.New(), custody.RegisteredHash, 10, null, null);
        var part = new PartitionPlanPart(PartitionPlanPartId.New(), 1, Hash("k"), 10, true);

        Assert.Throws<ArgumentException>(() => PartitionPlan.Create(
            PartitionPlanId.New(), custody.Tenant, custody.Project, withoutInspection, PartitionPolicy.RunbookDefault,
            Planner, Hash("plan"), PartitionPlanReason.SinglePartWithinTarget, [part], CorrelationId.New(), Now));
        Assert.Throws<ArgumentException>(() => PartitionPlan.Create(
            PartitionPlanId.New(), custody.Tenant, custody.Project, withoutEngine, PartitionPolicy.RunbookDefault,
            Planner, Hash("plan"), PartitionPlanReason.SinglePartWithinTarget, [part], CorrelationId.New(), Now));
    }

    [Fact]
    public void EveryReasonIsClassifiedIntoAnOutcome()
    {
        foreach (var reason in Enum.GetValues<PartitionPlanReason>())
        {
            var outcome = PartitionPlanReasons.OutcomeOf(reason);
            Assert.True(Enum.IsDefined(outcome), reason.ToString());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => PartitionPlanReasons.OutcomeOf((PartitionPlanReason)99));
    }

    [Fact]
    public void PersistedEnumValuesAreFrozenBecauseTheDatabaseStoresTheNumbers()
    {
        Assert.Equal(0, (int)PartitionPlanOutcome.Planned);
        Assert.Equal(1, (int)PartitionPlanOutcome.Unsupported);
        Assert.Equal(2, (int)PartitionPlanOutcome.Blocked);
        Assert.Equal(0, (int)PartitionPlanReason.SinglePartWithinTarget);
        Assert.Equal(1, (int)PartitionPlanReason.ItemInventoryUnavailable);
        Assert.Equal(2, (int)PartitionPlanReason.CanonicalInspectionUnavailable);
        Assert.Equal(3, (int)PartitionPlanReason.SourceHashDivergence);
        Assert.Equal(4, (int)PartitionPlanReason.StructuralDiagnosticNotValid);
        Assert.Equal(5, (int)PartitionPlanReason.ObservedSizeUnavailable);
    }

    [Fact]
    public void PlanningNeverMutatesTheCustodyRecord()
    {
        var custody = Custody(Hash("immutable"), size: 4096);
        var before = (custody.RegisteredHash, custody.RegisteredSizeBytes, custody.RelativePath.Value);

        _ = Plan(custody, Canonical(custody, 4096));

        Assert.Equal(before, (custody.RegisteredHash, custody.RegisteredSizeBytes, custody.RelativePath.Value));
    }

    [Fact]
    public void PlannerIdentityRejectsEmptyOrOversizedValues()
    {
        Assert.Throws<ArgumentException>(() => new PartitionPlannerIdentity(" ", "1.0"));
        Assert.Throws<ArgumentException>(() => new PartitionPlannerIdentity("planner", " "));
        Assert.Throws<ArgumentException>(() => new PartitionPlannerIdentity(new string('x', 101), "1.0"));
        Assert.Throws<ArgumentException>(() => new PartitionPlannerIdentity("planner", new string('x', 51)));
    }

    [Fact]
    public void SourceRejectsANegativeObservedSize() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PartitionPlanSource(
            ArtifactId.New(), null, Hash("h"), -1, null, null));

    [Fact]
    public void PolicyCanonicalJsonUsesInvariantFormattingForLargeNumbers()
    {
        // Nenhum separador de milhar/cultura pode vazar para a forma canônica (identidade determinística).
        var policy = PartitionPolicy.Create(1_234_567_890L, 9_876_543_210L);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{{\"hardPartBytes\":9876543210,\"targetPartBytes\":1234567890}}"),
            policy.CanonicalJson);
    }
}
