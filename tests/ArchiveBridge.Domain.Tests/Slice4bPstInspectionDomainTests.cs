using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// Slice 4B, Passo 1 (unitário, sem SQL) — invariantes de custódia (<see cref="MigrationArtifact"/>),
/// caminho relativo (<see cref="PstRelativePath"/>) e checkpoint de inspeção (<see cref="PstInspectionRecord"/>).
/// </summary>
public sealed class Slice4bPstInspectionDomainTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly DateTimeOffset Started = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset Completed = DateTimeOffset.UnixEpoch.AddSeconds(1);
    private const string EngineName = "TestEngine";
    private const string EngineVersion = "1.0.0";

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    // ---- PstRelativePath ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RelativePathRejectsEmpty(string value)
    {
        Assert.Throws<ArgumentException>(() => new PstRelativePath(value));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\system32")]
    public void RelativePathRejectsAbsolute(string value)
    {
        Assert.Throws<ArgumentException>(() => new PstRelativePath(value));
    }

    [Theory]
    [InlineData("../secrets/other.pst")]
    [InlineData("tenant/../../escape.pst")]
    [InlineData("./x.pst")]
    public void RelativePathRejectsTraversalSegments(string value)
    {
        Assert.Throws<ArgumentException>(() => new PstRelativePath(value));
    }

    [Fact]
    public void RelativePathAcceptsWellFormedValue()
    {
        var path = new PstRelativePath("tenant-a/project-1/mailbox.pst");
        Assert.Equal("tenant-a/project-1/mailbox.pst", path.Value);
    }

    // ---- MigrationArtifact (custody registration) ----

    [Fact]
    public void RegisterAssignsServerGeneratedIdentity()
    {
        var first = MigrationArtifact.Register(Tenant, Project, new PstRelativePath("a.pst"), Hash("a"), 10, Started);
        var second = MigrationArtifact.Register(Tenant, Project, new PstRelativePath("b.pst"), Hash("b"), 20, Started);

        Assert.NotEqual(Guid.Empty, first.Id.Value);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void RegisterRejectsEmptyTenant()
    {
        Assert.Throws<ArgumentException>(() =>
            MigrationArtifact.Register(default, Project, new PstRelativePath("a.pst"), Hash("a"), 10, Started));
    }

    [Fact]
    public void RegisterRejectsEmptyProject()
    {
        Assert.Throws<ArgumentException>(() =>
            MigrationArtifact.Register(Tenant, default, new PstRelativePath("a.pst"), Hash("a"), 10, Started));
    }

    [Fact]
    public void RegisterRejectsNegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MigrationArtifact.Register(Tenant, Project, new PstRelativePath("a.pst"), Hash("a"), -1, Started));
    }

    // ---- PstInspectionRecord ----

    private static PstInspectionRecord Complete(
        Sha256Hash expected, Sha256Hash? observed, long? size,
        PstStructuralDiagnostic diagnostic = PstStructuralDiagnostic.Valid,
        PstFormatVariant variant = PstFormatVariant.Unicode2013Plus) =>
        PstInspectionRecord.Complete(
            InspectionId.New(), Tenant, Project, ArtifactId.New(), expected, observed, size,
            diagnostic, variant, EngineName, EngineVersion, CorrelationId.New(), Started, Completed);

    [Fact]
    public void CompleteWithMatchingHashIsCanonical()
    {
        var hash = Hash("same");
        var record = Complete(hash, hash, 100);

        Assert.True(record.IsCanonical);
        Assert.Equal(PstInspectionOutcome.Completed, record.Outcome);
    }

    [Fact]
    public void CompleteWithDivergentHashIsNotCanonical()
    {
        // A criação de um Completed com hash observado diferente do esperado é uma composição inválida
        // do lado do CHAMADOR (a Application deveria ter escolhido Stale); o registro ainda assim nunca é
        // reportado como canônico — defesa em profundidade contra uso incorreto da fábrica.
        var record = Complete(Hash("expected"), Hash("different"), 100);

        Assert.False(record.IsCanonical);
    }

    [Fact]
    public void CompleteRejectsReadErrorWithHashPresent()
    {
        var hash = Hash("x");
        Assert.Throws<ArgumentException>(() =>
            Complete(hash, hash, 100, PstStructuralDiagnostic.ReadError));
    }

    [Fact]
    public void CompleteRejectsNonReadErrorWithoutHash()
    {
        Assert.Throws<ArgumentException>(() =>
            Complete(Hash("expected"), observed: null, size: null, PstStructuralDiagnostic.Valid));
    }

    [Fact]
    public void CompleteAllowsReadErrorWithoutHash()
    {
        var record = Complete(Hash("expected"), observed: null, size: null, PstStructuralDiagnostic.ReadError, PstFormatVariant.Unknown);

        Assert.Equal(PstInspectionOutcome.Completed, record.Outcome);
        Assert.Equal(PstStructuralDiagnostic.ReadError, record.Diagnostic);
        Assert.Null(record.ObservedHash);
        Assert.False(record.IsCanonical);
    }

    [Fact]
    public void StaleIsNeverCanonicalEvenWithHashPresent()
    {
        var record = PstInspectionRecord.Stale(
            InspectionId.New(), Tenant, Project, ArtifactId.New(), Hash("expected"), Hash("observed"), 50,
            EngineName, EngineVersion, CorrelationId.New(), Started, Completed);

        Assert.Equal(PstInspectionOutcome.Stale, record.Outcome);
        Assert.Null(record.Diagnostic);
        Assert.Null(record.FormatVariant);
        Assert.False(record.IsCanonical);
    }

    [Fact]
    public void LimitExceededIsNeverCanonical()
    {
        var record = PstInspectionRecord.LimitExceeded(
            InspectionId.New(), Tenant, Project, ArtifactId.New(), Hash("expected"), observedSizeBytes: null,
            EngineName, EngineVersion, CorrelationId.New(), Started, Completed);

        Assert.Equal(PstInspectionOutcome.LimitExceeded, record.Outcome);
        Assert.Null(record.ObservedHash);
        Assert.False(record.IsCanonical);
    }

    [Fact]
    public void CompletedAtBeforeStartedAtIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PstInspectionRecord.LimitExceeded(
                InspectionId.New(), Tenant, Project, ArtifactId.New(), Hash("expected"), observedSizeBytes: null,
                EngineName, EngineVersion, CorrelationId.New(), startedAtUtc: Completed, completedAtUtc: Started));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyEngineNameIsRejected(string engineName)
    {
        Assert.Throws<ArgumentException>(() =>
            PstInspectionRecord.LimitExceeded(
                InspectionId.New(), Tenant, Project, ArtifactId.New(), Hash("expected"), observedSizeBytes: null,
                engineName, EngineVersion, CorrelationId.New(), Started, Completed));
    }
}
