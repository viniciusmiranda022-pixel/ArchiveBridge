using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Security;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Security;

/// <summary>
/// AB-I7-008 item 3 — <see cref="BuildProvenanceRecord"/> e <see cref="ArtifactPromotionVerifier"/>:
/// identidade determinística verificável por SHA/digest, e drift entre build aprovada e artifact
/// promovido falha SEMPRE fechado (nunca silenciosamente aceito).
/// </summary>
public sealed class BuildProvenanceRecordTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private const string ValidCommitSha = "abc1234567890def1234567890abcdef12345678";

    [Fact]
    public void ApproveNormalizesTheCommitShaToLowercase()
    {
        var digest = new Sha256Hash(new string('a', 64));
        var record = BuildProvenanceRecord.Approve(
            Tenant, Project, "ArchiveBridge.Workers.Upload", artifactVersion: 1, ValidCommitSha.ToUpperInvariant(),
            "github-actions-runner", Now, digest, "ci-pipeline", "ServiceAccount", Correlation, Now);

        Assert.Equal(ValidCommitSha, record.SourceCommitSha);
    }

    [Theory]
    [InlineData("not-a-sha")]
    [InlineData("abc123")]
    [InlineData("zzzz567890def1234567890abcdef1234567890")]
    public void ApproveRejectsACommitShaThatIsNotAValidFortyHexCharacterSha1(string invalidSha)
    {
        var digest = new Sha256Hash(new string('a', 64));
        Assert.Throws<SupplyChainProvenanceInvariantViolationException>(() =>
            BuildProvenanceRecord.Approve(
                Tenant, Project, "ArchiveBridge.Workers.Upload", artifactVersion: 1, invalidSha,
                "github-actions-runner", Now, digest, "ci-pipeline", "ServiceAccount", Correlation, Now));
    }

    [Fact]
    public void VerifyPromotionSucceedsWhenTheCandidateDigestMatchesTheApprovedBuild()
    {
        var digest = new Sha256Hash(new string('a', 64));
        var approved = BuildProvenanceRecord.Approve(
            Tenant, Project, "ArchiveBridge.Workers.Upload", artifactVersion: 1, ValidCommitSha,
            "github-actions-runner", Now, digest, "ci-pipeline", "ServiceAccount", Correlation, Now);

        ArtifactPromotionVerifier.VerifyPromotion(approved, digest);
    }

    [Fact]
    public void VerifyPromotionFailsClosedWhenTheCandidateDigestDriftsFromTheApprovedBuild()
    {
        var approvedDigest = new Sha256Hash(new string('a', 64));
        var driftedDigest = new Sha256Hash(new string('b', 64));
        var approved = BuildProvenanceRecord.Approve(
            Tenant, Project, "ArchiveBridge.Workers.Upload", artifactVersion: 1, ValidCommitSha,
            "github-actions-runner", Now, approvedDigest, "ci-pipeline", "ServiceAccount", Correlation, Now);

        Assert.Throws<SupplyChainPromotionDriftException>(() => ArtifactPromotionVerifier.VerifyPromotion(approved, driftedDigest));
    }

    [Fact]
    public void RehydrateOfATamperedArtifactDigestIsRejectedFailClosed()
    {
        var digest = new Sha256Hash(new string('a', 64));
        var record = BuildProvenanceRecord.Approve(
            Tenant, Project, "ArchiveBridge.Workers.Upload", artifactVersion: 1, ValidCommitSha,
            "github-actions-runner", Now, digest, "ci-pipeline", "ServiceAccount", Correlation, Now);

        Assert.Throws<SupplyChainIntegrityViolationException>(() =>
            BuildProvenanceRecord.Rehydrate(
                Tenant, Project, record.ArtifactName, record.ArtifactVersion, record.SourceCommitSha,
                record.BuilderIdentity, record.BuildTimestampUtc, new Sha256Hash(new string('f', 64)),
                record.ApprovedBy, record.ApprovedByRole, record.Correlation, record.ApprovedAtUtc,
                record.SchemaVersion, record.ContentFingerprint, record.RecordHash));
    }

    [Fact]
    public void TwoApprovalsWithTheSameContentProduceTheSameContentFingerprint()
    {
        var digest = new Sha256Hash(new string('a', 64));
        var first = BuildProvenanceRecord.Approve(
            Tenant, Project, "ArchiveBridge.Workers.Upload", artifactVersion: 1, ValidCommitSha,
            "github-actions-runner", Now, digest, "ci-pipeline", "ServiceAccount", Correlation, Now);
        var second = BuildProvenanceRecord.Approve(
            Tenant, Project, "ArchiveBridge.Workers.Upload", artifactVersion: 2, ValidCommitSha,
            "github-actions-runner", Now, digest, "another-approver", "ServiceAccount", CorrelationId.New(), Now.AddMinutes(5));

        Assert.Equal(first.ContentFingerprint, second.ContentFingerprint);
        Assert.NotEqual(first.RecordHash, second.RecordHash);
    }
}
