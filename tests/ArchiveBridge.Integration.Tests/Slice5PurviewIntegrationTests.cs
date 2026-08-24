using System.Data;
using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// I5/EPIC-06 Passo 1 — capability registry &amp; mailbox/tenant prechecks sobre SQL real (AB-I5-001,
/// AB-I5-003): persistência append-only versionada, idempotência sob corrida, isolamento por
/// tenant/projeto (RLS), resolução server-side da mailbox canônica a partir de uma onda autorizada
/// (anti-IDOR) e falha fechada diante de evidência persistida adulterada/inconsistente.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice5PurviewIntegrationTests(SqlServerFixture fixture)
{
    private SqlCapabilityEvidenceStore CapabilityStore => new(fixture.Factory);

    private SqlMailboxPrecheckStore PrecheckStore => new(fixture.Factory);

    private SqlWaveStore WaveStore => Slice2Support.WaveStore(fixture);

    private static ArchiveRef ResolvedMailbox(string mailbox) => new(mailbox, new TargetArchiveId(mailbox));

    private static MailboxPrecheckObservation ValidObservation(
        MailboxArchiveStatus status = MailboxArchiveStatus.Active, long? observedAvailableBytes = 200_000_000_000) => new(
        Guid.NewGuid(), Guid.NewGuid(), status, "UserMailbox", AutoExpandingArchiveEnabled: false,
        LitigationHoldEnabled: false, RetentionHoldEnabled: false, ArchiveItemCount: 1000,
        ArchiveTotalSizeBytes: 10_000_000_000, observedAvailableBytes, DateTimeOffset.UtcNow);

    /// <summary>
    /// Persiste um projeto e uma onda com UMA entrada de archive RESOLVIDA (mailbox) sob <paramref name="scope"/> —
    /// fonte server-side autorizada que <see cref="SubmitMailboxPrecheckUseCase"/> deve resolver, nunca uma
    /// <see cref="ArchiveRef"/> fabricada pelo chamador.
    /// </summary>
    private async Task<MigrationWave> SeedWaveWithResolvedArchiveAsync(TenantScope scope, string mailbox)
    {
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var selection = new WaveSelection([Slice2Support.Entry($"{mailbox}.pst", mailbox, 1_000_000_000)]);
        var wave = Slice2Support.NewWave(scope, selection);
        await WaveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return wave;
    }

    // ---- Capability evidence: persistência, idempotência, isolamento --------------------------------

    [Fact]
    public async Task DiscoverPersistsGeneralAvailabilityForThePstImportRoute()
    {
        var scope = SqlServerFixture.NewScope();
        var useCase = new DiscoverPurviewCapabilityUseCase(CapabilityStore, new MutableClock(DateTimeOffset.UtcNow));

        var result = await useCase.ExecuteAsync(
            new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New()),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(CapabilityStatus.GeneralAvailability, result.Evidence.Status);

        var latest = await CapabilityStore.GetLatestAsync(scope, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(result.Evidence.EvidenceHash, latest!.EvidenceHash);
    }

    [Fact]
    public async Task RepeatedDiscoveryWithNoRealChangeDoesNotCreateANewVersion()
    {
        var scope = SqlServerFixture.NewScope();
        var useCase = new DiscoverPurviewCapabilityUseCase(CapabilityStore, new MutableClock(DateTimeOffset.UtcNow));
        var request = new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New());

        var first = await useCase.ExecuteAsync(request, CancellationToken.None);
        var second = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(1, first.Evidence.Version);
        Assert.Equal(first.Evidence.Id, second.Evidence.Id);
    }

    [Fact]
    public async Task CapabilityEvidenceFromAnotherProjectIsIndistinguishableFromNotFound()
    {
        var scopeA = SqlServerFixture.NewScope();
        var scopeB = SqlServerFixture.NewScope();
        var useCase = new DiscoverPurviewCapabilityUseCase(CapabilityStore, new MutableClock(DateTimeOffset.UtcNow));
        await useCase.ExecuteAsync(
            new DiscoverPurviewCapabilityRequest(scopeA, PurviewCapabilityRoutes.PstImport, CorrelationId.New()), CancellationToken.None);

        var fromOtherScope = await CapabilityStore.GetLatestAsync(scopeB, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport, CancellationToken.None);
        Assert.Null(fromOtherScope);
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenThePersistedEvidenceHashIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var useCase = new DiscoverPurviewCapabilityUseCase(CapabilityStore, new MutableClock(DateTimeOffset.UtcNow));
        var result = await useCase.ExecuteAsync(
            new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New()), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.purview_capability_evidence SET status = 0 WHERE evidence_id = @id;", // status adulterado para Unknown
            ("@id", result.Evidence.Id.Value));

        await Assert.ThrowsAsync<CapabilityEvidenceIntegrityViolationException>(
            () => CapabilityStore.GetLatestAsync(scope, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport, CancellationToken.None));
    }

    // ---- Mailbox precheck: persistência, idempotência, isolamento -----------------------------------

    [Fact]
    public async Task SubmitPersistsTheObservedPrecheckSnapshot()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveWithResolvedArchiveAsync(scope, "user01@contoso.com");
        var mailbox = ResolvedMailbox("user01@contoso.com");
        var adapter = new StubMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(WaveStore, PrecheckStore, adapter, new MutableClock(DateTimeOffset.UtcNow));

        var result = await useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, mailbox.Identity, CorrelationId.New()), CancellationToken.None);
        Assert.True(result.Created);

        var latest = await PrecheckStore.GetLatestAsync(scope, mailbox.Identity, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(MailboxArchiveStatus.Active, latest!.ArchiveStatus);
        Assert.Equal(result.Snapshot.SnapshotHash, latest.SnapshotHash);
        // A mailbox de exibição persistida é a CANÔNICA resolvida server-side pela onda.
        Assert.Equal("user01@contoso.com", latest.Mailbox.Mailbox);
    }

    [Fact]
    public async Task RepeatedSubmissionWithNoRealChangeDoesNotCreateANewVersion()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveWithResolvedArchiveAsync(scope, "user02@contoso.com");
        var mailbox = ResolvedMailbox("user02@contoso.com");
        var adapter = new StubMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(WaveStore, PrecheckStore, adapter, new MutableClock(DateTimeOffset.UtcNow));
        var request = new SubmitMailboxPrecheckRequest(scope, wave.Id, mailbox.Identity, CorrelationId.New());

        var first = await useCase.ExecuteAsync(request, CancellationToken.None);
        var second = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(1, first.Snapshot.Version);
    }

    [Fact]
    public async Task PrecheckFromAnotherProjectIsIndistinguishableFromNotFound()
    {
        var scopeA = SqlServerFixture.NewScope();
        var scopeB = SqlServerFixture.NewScope();
        var wave = await SeedWaveWithResolvedArchiveAsync(scopeA, "user03@contoso.com");
        var mailbox = ResolvedMailbox("user03@contoso.com");
        var adapter = new StubMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(WaveStore, PrecheckStore, adapter, new MutableClock(DateTimeOffset.UtcNow));
        await useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scopeA, wave.Id, mailbox.Identity, CorrelationId.New()), CancellationToken.None);

        var fromOtherScope = await PrecheckStore.GetLatestAsync(scopeB, mailbox.Identity, CancellationToken.None);
        Assert.Null(fromOtherScope);
    }

    [Fact]
    public async Task SubmitFailsClosedWhenTheWaveBelongsToAnotherTenantOrProject()
    {
        // Anti-IDOR (AB-I5-003) sob SQL/RLS real: a onda existe, mas para OUTRO tenant/projeto — o
        // chamador não consegue sondar/persistir precheck de um archive fora do seu próprio escopo, e o
        // adapter nunca é sondado.
        var owner = SqlServerFixture.NewScope();
        var attacker = SqlServerFixture.NewScope();
        var wave = await SeedWaveWithResolvedArchiveAsync(owner, "user06@contoso.com");
        var adapter = new StubMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(WaveStore, PrecheckStore, adapter, new MutableClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PurviewArchiveNotFoundException>(() => useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(attacker, wave.Id, new TargetArchiveId("user06@contoso.com"), CorrelationId.New()),
            CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task SubmitFailsClosedWhenTheArchiveIsNotPartOfTheWaveSelection()
    {
        // Um TargetArchiveId arbitrário que não pertence à seleção da onda autorizada falha fechado — sem
        // sondar o adapter — mesmo com a onda existindo no escopo correto.
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveWithResolvedArchiveAsync(scope, "user07@contoso.com");
        var adapter = new StubMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(WaveStore, PrecheckStore, adapter, new MutableClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PurviewArchiveNotFoundException>(() => useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, new TargetArchiveId("attacker-arbitrary@contoso.com"), CorrelationId.New()),
            CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenTheArchiveStatusColumnIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveWithResolvedArchiveAsync(scope, "user04@contoso.com");
        var mailbox = ResolvedMailbox("user04@contoso.com");
        var adapter = new StubMailboxPrecheckAdapter(ValidObservation(status: MailboxArchiveStatus.Disabled));
        var useCase = new SubmitMailboxPrecheckUseCase(WaveStore, PrecheckStore, adapter, new MutableClock(DateTimeOffset.UtcNow));
        var result = await useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, mailbox.Identity, CorrelationId.New()), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.purview_mailbox_prechecks SET archive_status = 3 WHERE snapshot_id = @id;", // Disabled (2) forjado para Active (3)
            ("@id", result.Snapshot.Id.Value));

        await Assert.ThrowsAsync<MailboxPrecheckIntegrityViolationException>(
            () => PrecheckStore.GetLatestAsync(scope, mailbox.Identity, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenObservedAvailableBytesIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveWithResolvedArchiveAsync(scope, "user05@contoso.com");
        var mailbox = ResolvedMailbox("user05@contoso.com");
        var adapter = new StubMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(WaveStore, PrecheckStore, adapter, new MutableClock(DateTimeOffset.UtcNow));
        var result = await useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, mailbox.Identity, CorrelationId.New()), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.purview_mailbox_prechecks SET observed_available_bytes = 999999999999 WHERE snapshot_id = @id;",
            ("@id", result.Snapshot.Id.Value));

        await Assert.ThrowsAsync<MailboxPrecheckIntegrityViolationException>(
            () => PrecheckStore.GetLatestAsync(scope, mailbox.Identity, CancellationToken.None));
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private async Task ExecuteAdminSqlAsync(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Duplo de teste da porta <see cref="IMailboxPrecheckAdapter"/> — determinístico, sem EXO/Graph.</summary>
internal sealed class StubMailboxPrecheckAdapter(MailboxPrecheckObservation observation) : IMailboxPrecheckAdapter
{
    /// <summary>Quantas vezes o adapter foi sondado — usado para provar que falhas fail-closed nunca sondam.</summary>
    public int ObserveCallCount { get; private set; }

    public Task<MailboxPrecheckObservation> ObserveAsync(
        TenantScope scope, ArchiveRef mailbox, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ObserveCallCount++;
        return Task.FromResult(observation);
    }
}
