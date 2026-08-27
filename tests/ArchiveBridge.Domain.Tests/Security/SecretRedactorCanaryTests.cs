using ArchiveBridge.Domain.Security;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Security;

/// <summary>
/// AB-I7-008 item 4/acceptance criteria 4 — regressão de redação de segredo/PII: injeta valores CANÁRIOS
/// (fake, mas com a forma realista de cada categoria) em strings representativas de log/evidência e prova
/// que a saída de <see cref="SecretRedactor.Redact"/> NUNCA contém o valor canário bruto. Também prova
/// que <see cref="SecretRedactor.ContainsSuspectedSecret"/> sinaliza cada categoria, usada como guarda
/// fail-closed pelos tipos de evidência deste Passo.
/// </summary>
public sealed class SecretRedactorCanaryTests
{
    private const string TenantScopeId = "tenant-canary-scope";

    public static TheoryData<string, string> CanarySamplesAndTheirSecretSubstring() => new()
    {
        { "Authorization: Bearer canary-jwt-eyJCANARYSECRETVALUE1234567890", "canary-jwt-eyJCANARYSECRETVALUE1234567890" },
        { "Cookie: session=canary-cookie-secret-XYZ987654321", "canary-cookie-secret-XYZ987654321" },
        {
            "GET https://contoso.blob.core.windows.net/c/f.pst?sv=2020&sig=CANARYSASSIGVALUE1234567890 HTTP/1.1",
            "CANARYSASSIGVALUE1234567890"
        },
        { "ConnectionString: AccountKey=CANARYACCOUNTKEYVALUE1234567890==;", "CANARYACCOUNTKEYVALUE1234567890" },
        { "sharedAccessSignature=CANARYSHAREDACCESSSIGNATUREVALUE", "CANARYSHAREDACCESSSIGNATUREVALUE" },
        { "bearer canary-standalone-bearer-token-999", "canary-standalone-bearer-token-999" },
        { "Migration owner: user.canary@contoso.com", "user.canary@contoso.com" },
        { @"Evidence source: \\fileserver01\share\canary-secret-path\file.pst", @"canary-secret-path" },
        { "Subject: Canary Confidential Merger Subject Line", "Canary Confidential Merger Subject Line" },
        { "Body: canary body content with confidential wording", "canary body content with confidential wording" },
        { "Attachment-Name: canary-secret-attachment.pst", "canary-secret-attachment.pst" },
    };

    [Theory]
    [MemberData(nameof(CanarySamplesAndTheirSecretSubstring))]
    public void RedactNeverLeavesTheCanarySubstringInTheOutput(string sample, string canarySubstring)
    {
        var redacted = SecretRedactor.Redact(sample, TenantScopeId);

        Assert.DoesNotContain(canarySubstring, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CanarySamplesAndTheirSecretSubstring))]
    public void ContainsSuspectedSecretFlagsEveryCanarySample(string sample, string canarySubstring)
    {
        _ = canarySubstring;
        Assert.True(SecretRedactor.ContainsSuspectedSecret(sample));
    }

    [Fact]
    public void RedactIsDeterministicForTheSameInputAndScope()
    {
        const string sample = "Migration owner: user.canary@contoso.com";

        var first = SecretRedactor.Redact(sample, TenantScopeId);
        var second = SecretRedactor.Redact(sample, TenantScopeId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RedactProducesDifferentUpnPlaceholdersForDifferentTenantScopes()
    {
        const string sample = "Migration owner: user.canary@contoso.com";

        var redactedForTenantA = SecretRedactor.Redact(sample, "tenant-a");
        var redactedForTenantB = SecretRedactor.Redact(sample, "tenant-b");

        Assert.NotEqual(redactedForTenantA, redactedForTenantB);
    }

    [Fact]
    public void RedactNeverEmbedsTheRawEmailAddressEvenInsideThePlaceholder()
    {
        const string sample = "Migration owner: user.canary@contoso.com";

        var redacted = SecretRedactor.Redact(sample, TenantScopeId);

        Assert.DoesNotContain('@', redacted);
    }

    [Fact]
    public void PlainTechnicalTextWithoutAnySecretShapeIsNotFlagged()
    {
        const string sample = "Job state transitioned from Queued to Running at 2026-08-26T09:00:00Z.";

        Assert.False(SecretRedactor.ContainsSuspectedSecret(sample));
        Assert.Equal(sample, SecretRedactor.Redact(sample, TenantScopeId));
    }
}
