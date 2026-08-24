using System.Globalization;
using System.Text.Json;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// I5/EPIC-06 Passo 2 (AB-I5-004) — validação fail-closed do SAS, redação central e ciclo de vida do
/// handle de custódia. Testes puros de Domain: sem SQL, sem DPAPI, sem I/O.
/// </summary>
public sealed class Slice5PurviewSasDomainTests
{
    private const string Secret = "SuperSecretSignatureValueThatMustNeverLeak1234567890";
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    private static string ValidSasUri(
        DateTimeOffset? expiresAtUtc = null,
        string permissions = "cw",
        string container = "ingestiondata",
        string host = "mystorageaccount123.blob.core.windows.net",
        string scheme = "https",
        string? extraQuery = null,
        string signature = Secret)
    {
        var expiry = expiresAtUtc ?? Now.AddHours(2);
        var expiryText = Uri.EscapeDataString(expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var query = $"sv=2022-11-02&se={expiryText}&sp={permissions}&sig={Uri.EscapeDataString(signature)}";
        if (extraQuery is not null)
        {
            query += "&" + extraQuery;
        }

        return $"{scheme}://{host}/{container}?{query}";
    }

    // ---- Aceitação --------------------------------------------------------------------------------

    [Fact]
    public void ValidHttpsSasOnAuthorizedHostAndContainerIsAccepted()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(), Now);

        Assert.True(result.Accepted);
        Assert.Equal("mystorageaccount123.blob.core.windows.net", result.AuthorizedHost);
        Assert.Equal("ingestiondata", result.AuthorizedContainer);
        Assert.NotNull(result.Fingerprint);
        Assert.NotNull(result.Secret);
        Assert.True(result.Permissions!.Create);
        Assert.True(result.Permissions.Write);
        Assert.False(result.Permissions.Delete);
    }

    [Fact]
    public void HostCaseIsAcceptedCaseInsensitively()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(host: "MyStorageAccount123.BLOB.CORE.WINDOWS.NET"), Now);
        Assert.True(result.Accepted);
    }

    // ---- Rejeição fail-closed -----------------------------------------------------------------------

    [Fact]
    public void MalformedUriIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate("not a uri at all", Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.MalformedUri, result.Reason);
    }

    [Fact]
    public void HttpSchemeIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(scheme: "http"), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.SchemeNotHttps, result.Reason);
    }

    [Fact]
    public void UserInfoInUrlIsRejected()
    {
        var uri = ValidSasUri().Replace(
            "https://mystorageaccount123", "https://attacker:pw@mystorageaccount123", StringComparison.Ordinal);
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.UserInfoPresent, result.Reason);
    }

    [Fact]
    public void FragmentInUrlIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri() + "#fragment", Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.FragmentPresent, result.Reason);
    }

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("blob.core.windows.net.attacker.com")] // sufixo aparece no MEIO — nunca no final real do host
    [InlineData("notblob.core.windows.net")] // "blob" sem o PONTO separador antes — não é o sufixo autorizado
    [InlineData("blob.core.windows.net")] // sufixo inteiro sem NENHUM label de storage account antes
    public void HostOutsideAuthorizedSuffixIsRejected(string host)
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(host: host), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.HostNotAuthorized, result.Reason);
    }

    [Fact]
    public void HostWithAValidStorageAccountLabelBeforeTheSuffixIsAccepted()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(host: "prod-tenant01.blob.core.windows.net"), Now);
        Assert.True(result.Accepted);
    }

    [Fact]
    public void ContainerDifferentFromIngestiondataIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(container: "otherdata"), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.ContainerNotAuthorized, result.Reason);
    }

    [Fact]
    public void ContainerCaseDifferenceIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(container: "IngestionData"), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.ContainerNotAuthorized, result.Reason);
    }

    [Fact]
    public void ExtraPathSegmentAfterContainerIsRejected()
    {
        var uri = ValidSasUri().Replace("/ingestiondata?", "/ingestiondata/extra?", StringComparison.Ordinal);
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.UnexpectedPath, result.Reason);
    }

    [Theory]
    [InlineData("sv")]
    [InlineData("se")]
    [InlineData("sp")]
    [InlineData("sig")]
    public void DuplicateCriticalParameterIsRejected(string parameterName)
    {
        var uri = ValidSasUri(extraQuery: $"{parameterName}=duplicate-value");
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.DuplicateCriticalParameter, result.Reason);
    }

    [Fact]
    public void StoredPolicyIdentifierReferenceIsRejected()
    {
        var uri = ValidSasUri(extraQuery: "si=mystoredpolicy");
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.StoredPolicyReferenceNotVerifiable, result.Reason);
    }

    [Fact]
    public void MissingSignedVersionIsRejected()
    {
        var uri = $"https://mystorageaccount123.blob.core.windows.net/ingestiondata?se=2026-08-24T12%3A00%3A00Z&sp=cw&sig=abc";
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.MissingCriticalParameter, result.Reason);
    }

    [Fact]
    public void MissingSignatureIsRejected()
    {
        var uri = "https://mystorageaccount123.blob.core.windows.net/ingestiondata?sv=2022-11-02&se=2026-08-24T12%3A00%3A00Z&sp=cw";
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.MissingCriticalParameter, result.Reason);
    }

    [Fact]
    public void MalformedExpiryIsRejected()
    {
        var uri = "https://mystorageaccount123.blob.core.windows.net/ingestiondata?sv=2022-11-02&se=not-a-date&sp=cw&sig=abc";
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.ExpiryMalformed, result.Reason);
    }

    [Fact]
    public void AlreadyExpiredIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(expiresAtUtc: Now.AddMinutes(-5)), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.ExpiryAlreadyExpiredOrTooSoon, result.Reason);
    }

    [Fact]
    public void ExpiryWithinMinimumMarginIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(
            ValidSasUri(expiresAtUtc: Now + PurviewSasIntakePolicy.MinimumValidityRemaining - TimeSpan.FromSeconds(1)), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.ExpiryAlreadyExpiredOrTooSoon, result.Reason);
    }

    [Fact]
    public void ExpiryBeyondMaximumWindowIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(
            ValidSasUri(expiresAtUtc: Now + PurviewSasIntakePolicy.MaximumValidityWindow + TimeSpan.FromMinutes(1)), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.ExpiryExceedsMaximumWindow, result.Reason);
    }

    [Fact]
    public void ExpiryExactlyAtMaximumWindowIsAccepted()
    {
        var result = PurviewSasIntakePolicy.Validate(
            ValidSasUri(expiresAtUtc: Now + PurviewSasIntakePolicy.MaximumValidityWindow), Now);
        Assert.True(result.Accepted);
    }

    [Fact]
    public void UnrecognizedPermissionLetterIsRejected()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(permissions: "cwz"), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.PermissionsUnrecognized, result.Reason);
    }

    [Theory]
    [InlineData("r")] // somente leitura — sem create/write, não serve para upload
    [InlineData("cwd")] // delete além do necessário
    [InlineData("cwl")] // list além do necessário
    [InlineData("racwdl")] // permissões amplas de container inteiro
    public void PermissionsOutsideUploadPolicyAreRejected(string permissions)
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(permissions: permissions), Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.PermissionsNotWithinUploadPolicy, result.Reason);
    }

    [Fact]
    public void ProtocolRestrictionOtherThanHttpsIsRejected()
    {
        var uri = ValidSasUri(extraQuery: "spr=https,http");
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.False(result.Accepted);
        Assert.Equal(PurviewSasRejectionReason.ProtocolRestrictionNotHttpsOnly, result.Reason);
    }

    [Fact]
    public void ProtocolRestrictionHttpsOnlyIsAccepted()
    {
        var uri = ValidSasUri(extraQuery: "spr=https");
        var result = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.True(result.Accepted);
    }

    // ---- Fingerprint ----------------------------------------------------------------------------

    [Fact]
    public void SameRawSasProducesTheSameFingerprint()
    {
        var uri = ValidSasUri();
        var first = PurviewSasIntakePolicy.Validate(uri, Now);
        var second = PurviewSasIntakePolicy.Validate(uri, Now);
        Assert.Equal(first.Fingerprint!.Value.Value, second.Fingerprint!.Value.Value);
    }

    [Fact]
    public void DifferentRawSasProducesADifferentFingerprint()
    {
        var first = PurviewSasIntakePolicy.Validate(ValidSasUri(signature: "sig-one-value-aaaaaaaaaaaaaaaaaaaa"), Now);
        var second = PurviewSasIntakePolicy.Validate(ValidSasUri(signature: "sig-two-value-bbbbbbbbbbbbbbbbbbbb"), Now);
        Assert.NotEqual(first.Fingerprint!.Value.Value, second.Fingerprint!.Value.Value);
    }

    [Fact]
    public void FingerprintNeverEqualsTheRawSecretOrAnySubstringOfIt()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(), Now);
        Assert.DoesNotContain(Secret, result.Fingerprint!.Value.Value, StringComparison.Ordinal);
    }

    // ---- Redação central (canary) -------------------------------------------------------------------

    [Fact]
    public void RedactedSecretToStringNeverPrintsTheValue()
    {
        var secret = RedactedSecret.Wrap(Secret);
        Assert.Equal("[REDACTED]", secret.ToString());
        Assert.DoesNotContain(Secret, secret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RedactedSecretHasNoPublicDataMemberAndSerializesToAnEmptyObject()
    {
        var secret = RedactedSecret.Wrap(Secret);
        var json = JsonSerializer.Serialize(secret);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void RedactedSecretInterpolatedIntoAnExceptionMessageNeverLeaksTheValue()
    {
        var secret = RedactedSecret.Wrap(Secret);
        var exception = new InvalidOperationException($"Falha ao processar segredo: {secret}");
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrappingAnEmptySecretIsRejectedFailClosed()
    {
        Assert.Throws<ArgumentException>(() => RedactedSecret.Wrap(string.Empty));
        Assert.Throws<ArgumentException>(() => RedactedSecret.Wrap("   "));
    }

    [Fact]
    public void RejectedValidationResultNeverCarriesTheSecretOrAnyNonSecretMetadata()
    {
        var result = PurviewSasIntakePolicy.Validate(ValidSasUri(container: "wrong"), Now);
        Assert.False(result.Accepted);
        Assert.Null(result.Secret);
        Assert.Null(result.Fingerprint);
        Assert.Null(result.AuthorizedHost);
        Assert.Null(result.AuthorizedContainer);
        Assert.Null(result.ExpiresAtUtc);
        Assert.Null(result.Permissions);
    }

    // ---- Ciclo de vida do handle ------------------------------------------------------------------

    private static PurviewSasUploadHandle NewStoredHandle(int generation = 1) =>
        PurviewSasUploadHandle.Intake(
            SasHandleId.New(), new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()), new WaveId(Guid.NewGuid()),
            generation, new Sha256Hash(new string('a', 64)), new SecretStoreHandleReference("ref-1"),
            "mystorageaccount123.blob.core.windows.net", "ingestiondata", keyVersion: null, Now.AddHours(2),
            CorrelationId.New(), Now);

    [Fact]
    public void IntakeStartsInStoredState()
    {
        var handle = NewStoredHandle();
        Assert.Equal(SasHandleState.Stored, handle.State);
        Assert.Null(handle.AvailableAtUtc);
        Assert.Null(handle.ConsumedAtUtc);
        Assert.Null(handle.ExpiredAtUtc);
        Assert.Null(handle.DestroyedAtUtc);
    }

    [Fact]
    public void FullHappyPathLifecycleTransitionsInOrder()
    {
        var handle = NewStoredHandle();
        var available = handle.MarkAvailable(Now.AddMinutes(1));
        Assert.Equal(SasHandleState.Available, available.State);
        Assert.NotNull(available.AvailableAtUtc);

        var claimed = available.Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(6), Now.AddMinutes(2));
        Assert.Equal(SasHandleState.Claimed, claimed.State);
        Assert.Equal(WorkloadIdentities.UploadWorker, claimed.ClaimOwner);
        Assert.Equal(1, claimed.ClaimEpoch.Value);

        var consumed = claimed.FinalizeClaim(WorkloadIdentities.UploadWorker, claimed.ClaimEpoch, Now.AddMinutes(3));
        Assert.Equal(SasHandleState.Consumed, consumed.State);
        Assert.NotNull(consumed.ConsumedAtUtc);

        var destroyed = consumed.Destroy(Now.AddMinutes(4));
        Assert.Equal(SasHandleState.Destroyed, destroyed.State);
        Assert.NotNull(destroyed.DestroyedAtUtc);
    }

    [Fact]
    public void MarkAvailableFromNonStoredStateThrows()
    {
        var available = NewStoredHandle().MarkAvailable(Now);
        Assert.Throws<PurviewSasLifecycleException>(() => available.MarkAvailable(Now));
    }

    // ---- Claim / Reclaim / FinalizeClaim (AB-I5-006 item 2: lease/fencing por época) ------------------

    [Fact]
    public void ClaimFromNonAvailableStateThrows()
    {
        var stored = NewStoredHandle();
        Assert.Throws<PurviewSasLifecycleException>(() => stored.Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now));

        var claimedOnce = stored.MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        Assert.Throws<PurviewSasLifecycleException>(() => claimedOnce.Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now));
    }

    [Fact]
    public void ClaimWithANonFutureLeaseExpiryThrows()
    {
        var available = NewStoredHandle().MarkAvailable(Now);
        Assert.Throws<ArgumentOutOfRangeException>(() => available.Claim(WorkloadIdentities.UploadWorker, Now, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => available.Claim(WorkloadIdentities.UploadWorker, Now.AddSeconds(-1), Now));
    }

    [Fact]
    public void ClaimIncrementsTheEpochEachTimeItIsReivindicated()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        Assert.Equal(1, claimed.ClaimEpoch.Value);
        Assert.Equal(WorkloadIdentities.UploadWorker, claimed.ClaimOwner);
        Assert.NotNull(claimed.ClaimExpiresAtUtc);
    }

    [Fact]
    public void FinalizeClaimUnderTheCorrectOwnerAndEpochTransitionsToConsumed()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        var consumed = claimed.FinalizeClaim(WorkloadIdentities.UploadWorker, claimed.ClaimEpoch, Now.AddMinutes(1));

        Assert.Equal(SasHandleState.Consumed, consumed.State);
        Assert.NotNull(consumed.ConsumedAtUtc);
        // O owner/época do claim titular permanecem no registro mesmo após a finalização (trilha de quem consumiu).
        Assert.Equal(WorkloadIdentities.UploadWorker, consumed.ClaimOwner);
        Assert.Equal(claimed.ClaimEpoch, consumed.ClaimEpoch);
    }

    [Fact]
    public void FinalizeClaimFromNonClaimedStateThrows()
    {
        var available = NewStoredHandle().MarkAvailable(Now);
        Assert.Throws<PurviewSasLifecycleException>(
            () => available.FinalizeClaim(WorkloadIdentities.UploadWorker, LeaseEpoch.Initial.Next(), Now));
    }

    [Fact]
    public void FinalizeClaimWithTheWrongOwnerIsRejectedByFencing()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        Assert.Throws<PurviewSasLifecycleException>(
            () => claimed.FinalizeClaim(new WorkloadIdentity("SomeoneElse"), claimed.ClaimEpoch, Now));
    }

    [Fact]
    public void FinalizeClaimWithAStaleEpochIsRejectedByFencing()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        var staleEpoch = claimed.ClaimEpoch;
        var reclaimed = claimed.Reclaim(WorkloadIdentities.UploadWorker, Now.AddMinutes(10), Now.AddMinutes(6));

        // O MESMO owner, mas com a época ANTIGA (do claim já reassumido) — nunca finaliza (item 2: "owner
        // antigo não pode finalizar/reabrir claim reassumido").
        Assert.NotEqual(staleEpoch, reclaimed.ClaimEpoch);
        Assert.Throws<PurviewSasLifecycleException>(() => reclaimed.FinalizeClaim(WorkloadIdentities.UploadWorker, staleEpoch, Now.AddMinutes(6)));
    }

    [Fact]
    public void ReclaimBeforeTheLeaseExpiresIsRejectedFailClosed()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        Assert.Throws<PurviewSasLifecycleException>(
            () => claimed.Reclaim(new WorkloadIdentity("AnotherWorker"), Now.AddMinutes(10), Now.AddMinutes(1)));
    }

    [Fact]
    public void ReclaimExactlyAtLeaseExpiryIsAccepted()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        var reclaimed = claimed.Reclaim(WorkloadIdentities.UploadWorker, Now.AddMinutes(11), Now.AddMinutes(5));

        Assert.Equal(SasHandleState.Claimed, reclaimed.State);
        Assert.Equal(2, reclaimed.ClaimEpoch.Value);
    }

    [Fact]
    public void ReclaimFromNonClaimedStateThrows()
    {
        var available = NewStoredHandle().MarkAvailable(Now);
        Assert.Throws<PurviewSasLifecycleException>(
            () => available.Reclaim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now));
    }

    [Fact]
    public void ReclaimRotatesTheOwnerAndTheOldOwnerCanNeverFinalizeAgain()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(new WorkloadIdentity("OldOwner"), Now.AddMinutes(5), Now);
        var reclaimed = claimed.Reclaim(new WorkloadIdentity("NewOwner"), Now.AddMinutes(11), Now.AddMinutes(5));

        Assert.Equal(new WorkloadIdentity("NewOwner"), reclaimed.ClaimOwner);
        Assert.Throws<PurviewSasLifecycleException>(
            () => reclaimed.FinalizeClaim(new WorkloadIdentity("OldOwner"), reclaimed.ClaimEpoch, Now.AddMinutes(6)));

        var consumed = reclaimed.FinalizeClaim(new WorkloadIdentity("NewOwner"), reclaimed.ClaimEpoch, Now.AddMinutes(6));
        Assert.Equal(SasHandleState.Consumed, consumed.State);
    }

    [Fact]
    public void ExpiredAndDestroyedNeverTransitionBackToAvailable()
    {
        var expired = NewStoredHandle().MarkAvailable(Now).MarkExpired(Now);
        Assert.Throws<PurviewSasLifecycleException>(() => expired.MarkAvailable(Now));

        var destroyed = expired.Destroy(Now);
        Assert.Throws<PurviewSasLifecycleException>(() => destroyed.MarkAvailable(Now));
    }

    [Fact]
    public void ClaimedCanTransitionDirectlyToExpiredOrBeDestroyed()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);

        var expired = claimed.MarkExpired(Now.AddMinutes(1));
        Assert.Equal(SasHandleState.Expired, expired.State);

        var destroyed = claimed.Destroy(Now.AddMinutes(1));
        Assert.Equal(SasHandleState.Destroyed, destroyed.State);
    }

    [Fact]
    public void MarkExpiredFromAnAlreadyTerminalStateThrows()
    {
        var expired = NewStoredHandle().MarkAvailable(Now).MarkExpired(Now);
        Assert.Throws<PurviewSasLifecycleException>(() => expired.MarkExpired(Now));

        var destroyed = NewStoredHandle().Destroy(Now);
        Assert.Throws<PurviewSasLifecycleException>(() => destroyed.MarkExpired(Now));
    }

    [Fact]
    public void DestroyIsIdempotentFromAnyState()
    {
        var stored = NewStoredHandle();
        var destroyedOnce = stored.Destroy(Now);
        var destroyedTwice = destroyedOnce.Destroy(Now.AddMinutes(10));

        Assert.Equal(SasHandleState.Destroyed, destroyedOnce.State);
        Assert.Same(destroyedOnce, destroyedTwice); // no-op verdadeiro: mesma instância, nenhuma nova mutação.
    }

    [Fact]
    public void RehydrateFailsClosedWhenHandleHashDoesNotMatchLoadedFields()
    {
        var handle = NewStoredHandle();
        var tamperedFingerprint = new Sha256Hash(new string('b', 64)); // campo divergente do hash persistido

        Assert.Throws<PurviewSasHandleIntegrityViolationException>(() => PurviewSasUploadHandle.Rehydrate(
            handle.Id, handle.Tenant, handle.Project, handle.Wave, handle.Generation, handle.State, tamperedFingerprint,
            handle.SecretStoreReference, handle.AuthorizedHost, handle.AuthorizedContainer, handle.KeyVersion,
            handle.ExpiresAtUtc, handle.StoredAtUtc, handle.AvailableAtUtc, handle.ConsumedAtUtc, handle.ExpiredAtUtc,
            handle.DestroyedAtUtc, handle.ClaimOwner, handle.ClaimEpoch, handle.ClaimExpiresAtUtc, handle.Correlation,
            handle.RecordedAtUtc, RowVersion.None, handle.HandleHash));
    }

    [Fact]
    public void RehydrateFailsClosedWhenTheClaimOwnerIsTamperedIndependentlyOfTheHash()
    {
        // Um claim ativo cujo owner é forjado (ex.: linha adulterada diretamente no SQL) diverge do
        // handle_hash persistido — a fronteira NÃO CONFIÁVEL cobre TAMBÉM os campos de claim/fencing, não
        // só os campos herdados de antes de AB-I5-006.
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        var tamperedOwner = new WorkloadIdentity("AttackerControlled");

        Assert.Throws<PurviewSasHandleIntegrityViolationException>(() => PurviewSasUploadHandle.Rehydrate(
            claimed.Id, claimed.Tenant, claimed.Project, claimed.Wave, claimed.Generation, claimed.State, claimed.Fingerprint,
            claimed.SecretStoreReference, claimed.AuthorizedHost, claimed.AuthorizedContainer, claimed.KeyVersion,
            claimed.ExpiresAtUtc, claimed.StoredAtUtc, claimed.AvailableAtUtc, claimed.ConsumedAtUtc, claimed.ExpiredAtUtc,
            claimed.DestroyedAtUtc, tamperedOwner, claimed.ClaimEpoch, claimed.ClaimExpiresAtUtc, claimed.Correlation,
            claimed.RecordedAtUtc, RowVersion.None, claimed.HandleHash));
    }

    [Fact]
    public void RehydrateFailsClosedWhenTheClaimEpochIsTamperedIndependentlyOfTheHash()
    {
        // Uma época de fencing forjada para trás (ex.: um invasor tentando "reabrir" um claim antigo) é
        // pega pela mesma fronteira NÃO CONFIÁVEL — nunca confiada implicitamente porque "veio do SQL".
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        var tamperedEpoch = LeaseEpoch.Initial;

        Assert.Throws<PurviewSasHandleIntegrityViolationException>(() => PurviewSasUploadHandle.Rehydrate(
            claimed.Id, claimed.Tenant, claimed.Project, claimed.Wave, claimed.Generation, claimed.State, claimed.Fingerprint,
            claimed.SecretStoreReference, claimed.AuthorizedHost, claimed.AuthorizedContainer, claimed.KeyVersion,
            claimed.ExpiresAtUtc, claimed.StoredAtUtc, claimed.AvailableAtUtc, claimed.ConsumedAtUtc, claimed.ExpiredAtUtc,
            claimed.DestroyedAtUtc, claimed.ClaimOwner, tamperedEpoch, claimed.ClaimExpiresAtUtc, claimed.Correlation,
            claimed.RecordedAtUtc, RowVersion.None, claimed.HandleHash));
    }

    [Fact]
    public void RehydrateSucceedsWhenAllLoadedFieldsMatchThePersistedHash()
    {
        var handle = NewStoredHandle();
        var rehydrated = PurviewSasUploadHandle.Rehydrate(
            handle.Id, handle.Tenant, handle.Project, handle.Wave, handle.Generation, handle.State, handle.Fingerprint,
            handle.SecretStoreReference, handle.AuthorizedHost, handle.AuthorizedContainer, handle.KeyVersion,
            handle.ExpiresAtUtc, handle.StoredAtUtc, handle.AvailableAtUtc, handle.ConsumedAtUtc, handle.ExpiredAtUtc,
            handle.DestroyedAtUtc, handle.ClaimOwner, handle.ClaimEpoch, handle.ClaimExpiresAtUtc, handle.Correlation,
            handle.RecordedAtUtc, RowVersion.None, handle.HandleHash);

        Assert.Equal(handle.HandleHash.Value, rehydrated.HandleHash.Value);
        Assert.Equal(SasHandleState.Stored, rehydrated.State);
    }

    [Fact]
    public void RehydrateSucceedsForAClaimedHandleWhenAllLoadedFieldsMatchThePersistedHash()
    {
        var claimed = NewStoredHandle().MarkAvailable(Now).Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now);
        var rehydrated = PurviewSasUploadHandle.Rehydrate(
            claimed.Id, claimed.Tenant, claimed.Project, claimed.Wave, claimed.Generation, claimed.State, claimed.Fingerprint,
            claimed.SecretStoreReference, claimed.AuthorizedHost, claimed.AuthorizedContainer, claimed.KeyVersion,
            claimed.ExpiresAtUtc, claimed.StoredAtUtc, claimed.AvailableAtUtc, claimed.ConsumedAtUtc, claimed.ExpiredAtUtc,
            claimed.DestroyedAtUtc, claimed.ClaimOwner, claimed.ClaimEpoch, claimed.ClaimExpiresAtUtc, claimed.Correlation,
            claimed.RecordedAtUtc, RowVersion.None, claimed.HandleHash);

        Assert.Equal(claimed.HandleHash.Value, rehydrated.HandleHash.Value);
        Assert.Equal(SasHandleState.Claimed, rehydrated.State);
        Assert.Equal(WorkloadIdentities.UploadWorker, rehydrated.ClaimOwner);
    }

    // ---- Identidade de workload ----------------------------------------------------------------------

    [Fact]
    public void UploadWorkerIdentityMatchesTheDocumentedAdr0008Label()
    {
        Assert.Equal("ArchiveBridge-UploadWorker", WorkloadIdentities.UploadWorker.Value);
    }
}
