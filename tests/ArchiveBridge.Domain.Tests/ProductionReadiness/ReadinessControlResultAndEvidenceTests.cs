using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;
using Xunit;

namespace ArchiveBridge.Domain.Tests.ProductionReadiness;

/// <summary>AB-I8-001 — <see cref="ReadinessControlResult"/>/<see cref="ReadinessEvidenceReference"/>: Pass exige evidência real, e nenhum texto com aparência de segredo/PII é aceito.</summary>
public sealed class ReadinessControlResultAndEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    [Fact]
    public void PassWithoutEvidenceIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ReadinessControlResult.Create(
                new ReadinessControlId("ARCH.ADR_APPROVED"), ReadinessGateGroup.Architecture, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.None, reasonCode: string.Empty, Now));
    }

    [Fact]
    public void NotMeasuredFactoryUsesTheCanonicalNoEvidenceReference()
    {
        var result = ReadinessControlResult.NotMeasured(new ReadinessControlId("ARCH.ADR_APPROVED"), ReadinessGateGroup.Architecture, "PENDING", Now);

        Assert.Equal(ReadinessEvidenceKind.None, result.Evidence.Kind);
        Assert.Equal(ReadinessEvidenceReference.NoEvidenceFingerprint, result.Evidence.Fingerprint);
    }

    [Theory]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.abc.def")]
    [InlineData("sig=abcdef123456&se=2026-01-01")]
    [InlineData("operator@contoso.com approved this")]
    public void EvidenceLocatorsThatLookLikeSecretsAreRejected(string suspectLocator)
    {
        Assert.Throws<ArgumentException>(() => ReadinessEvidenceReference.SystemDerived(SomeFingerprint, suspectLocator));
        Assert.Throws<ArgumentException>(() => ReadinessEvidenceReference.Attested(SomeFingerprint, suspectLocator));
    }

    [Fact]
    public void ANormalLocatorIsAccepted()
    {
        var evidence = ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "recovery-readiness:RestoreDrill:v12");
        Assert.Equal("recovery-readiness:RestoreDrill:v12", evidence.Locator);
    }

    [Fact]
    public void TheNoneInstanceCarriesTheCanonicalFingerprintAndAnEmptyLocator()
    {
        Assert.Equal(ReadinessEvidenceKind.None, ReadinessEvidenceReference.None.Kind);
        Assert.Equal(ReadinessEvidenceReference.NoEvidenceFingerprint, ReadinessEvidenceReference.None.Fingerprint);
        Assert.Equal(string.Empty, ReadinessEvidenceReference.None.Locator);
    }
}
