using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>Testes de domínio de capability registry (I5/EPIC-06 Passo 1, AB-I5-001).</summary>
public sealed class Slice5PurviewCapabilityDomainTests
{
    private static TenantId Tenant => new(Guid.NewGuid());

    private static ProjectId Project => new(Guid.NewGuid());

    // ---- PurviewCapabilityCatalog (honestidade comercial) ------------------------------------------

    [Fact]
    public void KnownPstImportRouteIsGeneralAvailability()
    {
        var entry = PurviewCapabilityCatalog.Describe(PurviewCapabilityRoutes.PstImport);
        Assert.Equal(CapabilityStatus.GeneralAvailability, entry.Status);
        Assert.False(string.IsNullOrWhiteSpace(entry.SourceReference));
    }

    [Fact]
    public void UnknownRouteIsNeverInferredAsSupported()
    {
        var entry = PurviewCapabilityCatalog.Describe(new PurviewCapabilityRoute("Purview.NetworkUpload.SomeFutureRoute"));
        Assert.Equal(CapabilityStatus.Unknown, entry.Status);
    }

    // ---- CapabilityEvidence (tamper-evidence) -------------------------------------------------------

    [Fact]
    public void RecordThenRehydrateRoundTripsWithMatchingHash()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, now, CorrelationId.New(), now);

        var rehydrated = CapabilityEvidence.Rehydrate(
            evidence.Id, evidence.Tenant, evidence.Project, evidence.Provider, evidence.Route, evidence.Version,
            evidence.Status, evidence.SourceReference, evidence.DocumentationVersion, evidence.CapabilityVersionLabel,
            evidence.ObservedAtUtc, evidence.Correlation, evidence.RecordedAtUtc, evidence.EvidenceHash);

        Assert.Equal(evidence.EvidenceHash, rehydrated.EvidenceHash);
    }

    [Fact]
    public void RehydrateFailsClosedWhenStatusIsTamperedButHashStaysStale()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, now, CorrelationId.New(), now);

        // Simula adulteração isolada de uma linha persistida: status mudado, hash gravado permanece o antigo.
        Assert.Throws<CapabilityEvidenceIntegrityViolationException>(() => CapabilityEvidence.Rehydrate(
            evidence.Id, evidence.Tenant, evidence.Project, evidence.Provider, evidence.Route, evidence.Version,
            CapabilityStatus.Unsupported, evidence.SourceReference, evidence.DocumentationVersion, evidence.CapabilityVersionLabel,
            evidence.ObservedAtUtc, evidence.Correlation, evidence.RecordedAtUtc, evidence.EvidenceHash));
    }

    [Fact]
    public void IsSameContentAsIgnoresRecordedAtUtcButNotStatus()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var first = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, observedAt, CorrelationId.New(), observedAt);

        var laterRediscovery = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), first.Tenant, first.Project, first.Provider, first.Route, 2,
            CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, observedAt, CorrelationId.New(),
            observedAt.AddDays(30));

        Assert.True(first.IsSameContentAs(laterRediscovery));

        var downgraded = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), first.Tenant, first.Project, first.Provider, first.Route, 3,
            CapabilityStatus.Unknown, null, null, null, observedAt, CorrelationId.New(), observedAt.AddDays(60));

        Assert.False(first.IsSameContentAs(downgraded));
    }

    // ---- CapabilityEvidencePolicy (fail-closed) -------------------------------------------------------

    [Fact]
    public void NoEvidenceIsNeverUsable()
    {
        var outcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(null, DateTimeOffset.UtcNow, TimeSpan.FromDays(1));
        Assert.Equal(CapabilityUsabilityOutcome.NoEvidence, outcome);
    }

    [Fact]
    public void UnknownStatusBlocks()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, CapabilityStatus.Unknown, null, null, null, now, CorrelationId.New(), now);

        var outcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(evidence, now, TimeSpan.FromDays(180));
        Assert.Equal(CapabilityUsabilityOutcome.Unknown, outcome);
    }

    [Theory]
    [InlineData(CapabilityStatus.Preview)]
    [InlineData(CapabilityStatus.Contractual)]
    public void PreviewOrContractualIsNeverTreatedAsGa(CapabilityStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, status, "some-doc", null, null, now, CorrelationId.New(), now);

        var outcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(evidence, now, TimeSpan.FromDays(180));
        Assert.Equal(CapabilityUsabilityOutcome.NotGeneralAvailability, outcome);
    }

    [Fact]
    public void GeneralAvailabilityWithinFreshnessWindowIsUsable()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, now.AddYears(-1), CorrelationId.New(), now);

        var outcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(evidence, now, TimeSpan.FromDays(180));
        Assert.Equal(CapabilityUsabilityOutcome.Usable, outcome);
    }

    [Fact]
    public void EvidenceOlderThanMaxAgeSinceLastRecordedIsStale()
    {
        var recordedAt = DateTimeOffset.UtcNow;
        var evidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, recordedAt, CorrelationId.New(), recordedAt);

        var now = recordedAt + TimeSpan.FromDays(181);
        var outcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(evidence, now, TimeSpan.FromDays(180));
        Assert.Equal(CapabilityUsabilityOutcome.Stale, outcome);
    }

    [Fact]
    public void DowngradeToLatestEvidenceAlwaysWinsOverAnOlderHigherStatus()
    {
        // Simula: a evidência mais recente é a que decide, nunca a "melhor" historicamente (item 13).
        var now = DateTimeOffset.UtcNow;
        var stale = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, now, CorrelationId.New(), now);
        var downgraded = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), Tenant, Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            2, CapabilityStatus.Unsupported, null, null, null, now, CorrelationId.New(), now);

        // O caller sempre passa o REGISTRO MAIS RECENTE (maior Version) — a política nunca escolhe o "melhor".
        var outcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(downgraded, now, TimeSpan.FromDays(180));
        Assert.Equal(CapabilityUsabilityOutcome.Unsupported, outcome);
        Assert.NotEqual(stale.Status, downgraded.Status);
    }
}
