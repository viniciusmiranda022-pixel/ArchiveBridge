using System.Globalization;
using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Testes de Application do intake/aquisição/destruição do SAS custodiado (I5/EPIC-06 Passo 2, AB-I5-004)
/// — provam que os casos de uso são testáveis só com Domain + Contracts, sem DPAPI/SQL reais. Os testes
/// sob SQL Server real (isolamento RLS, concorrência genuína, índice único filtrado) vivem em
/// <c>Slice5PurviewSasIntegrationTests</c> (Integration.Tests).
/// </summary>
public sealed class Slice5PurviewSasUseCaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static TenantScope Scope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static string ValidSasUri(TenantScope scope, DateTimeOffset? expiresAtUtc = null) =>
        // O host/container/permissões não carregam nenhum dado de escopo — o fingerprint varia por
        // scope apenas para produzir referências distintas entre testes que usam escopos diferentes.
        $"https://mystorageaccount123.blob.core.windows.net/ingestiondata?sv=2022-11-02&se=" +
        Uri.EscapeDataString((expiresAtUtc ?? Now.AddHours(2)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)) +
        $"&sp=cw&sig={Uri.EscapeDataString($"sig-{scope.Tenant.Value:N}-{Guid.NewGuid():N}")}";

    private static MigrationWave SeedWave(FakeWaveStore waves, TenantScope scope)
    {
        var entry = new WaveEntry(
            "prj01-w001", "p_000.pst", new ArchiveRef("user01@contoso.com", new TargetArchiveId("user01@contoso.com")),
            sizeBytes: 1_000_000_000, itemCount: 10);
        var wave = MigrationWave.Create(
            WaveId.New(), scope.Tenant, scope.Project, new WaveName("Wave 1"), TargetRootFolder.ForWave("PRJ01", "W001"),
            DeterministicHash.Compute(["config"]), new WaveSelection([entry]), Now);
        waves.Seed(wave);
        return wave;
    }

    // ---- IntakePurviewSasUseCase --------------------------------------------------------------------

    [Fact]
    public async Task IntakeValidatesProtectsAndPersistsAnAvailableHandle()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now));

        var result = await useCase.ExecuteAsync(
            new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(SasHandleState.Available, result.State);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, secrets.ProtectCallCount);

        var canonical = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.NotNull(canonical);
        Assert.Equal(result.Id, canonical!.Id);
    }

    [Fact]
    public async Task IntakeFailsClosedWhenTheWaveDoesNotExistAndNeverProtectsTheSecret()
    {
        var scope = Scope();
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new IntakePurviewSasUseCase(new FakeWaveStore(), handles, secrets, new StubClock(Now));

        await Assert.ThrowsAsync<PurviewWaveNotFoundException>(() => useCase.ExecuteAsync(
            new IntakePurviewSasRequest(scope, WaveId.New(), RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()),
            CancellationToken.None));

        Assert.Equal(0, secrets.ProtectCallCount);
    }

    [Fact]
    public async Task IntakeRejectsAnInvalidSasAndNeverProtectsIt()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now));

        var invalidSas = "https://evil.example.com/ingestiondata?sv=2022-11-02&se=2026-08-24T12%3A00%3A00Z&sp=cw&sig=x";

        var exception = await Assert.ThrowsAsync<PurviewSasIntakeRejectedException>(() => useCase.ExecuteAsync(
            new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(invalidSas), CorrelationId.New()),
            CancellationToken.None));

        Assert.Equal(PurviewSasRejectionReason.HostNotAuthorized, exception.Reason);
        Assert.Equal(0, secrets.ProtectCallCount);
        Assert.Null(await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ANewIntakeForTheSameWaveVersionsAndDestroysThePreviousGeneration()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now));

        var first = await useCase.ExecuteAsync(
            new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()),
            CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(1, first.Generation);
        Assert.Equal(2, second.Generation);
        Assert.NotEqual(first.Id, second.Id);

        var canonical = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(second.Id, canonical!.Id);

        var previous = await handles.GetByIdAsync(scope, first.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Destroyed, previous!.State);
    }

    [Fact]
    public async Task IntakeConvergesUnderConcurrentReplaceInsteadOfFailingPermanently()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now));

        // Simula outro intake concorrente que ocupa a geração 1 exatamente entre a leitura do canônico e
        // a substituição desta execução — a execução corrente deve reler e convergir para a geração 2.
        var raced = false;
        handles.BeforeReplaceAttempt = () =>
        {
            if (!raced)
            {
                raced = true;
                handles.SeedDirectly(PurviewSasUploadHandle.Intake(
                    SasHandleId.New(), scope.Tenant, scope.Project, wave.Id, 1, new Sha256Hash(new string('c', 64)),
                    new SecretStoreHandleReference("concurrent-ref"), "mystorageaccount123.blob.core.windows.net",
                    "ingestiondata", null, Now.AddHours(1), CorrelationId.New(), Now));
            }
        };

        var result = await useCase.ExecuteAsync(
            new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(2, result.Generation);
        // A proteção do segredo ocorre UMA única vez mesmo sob a corrida (só a gravação do metadado reexecuta).
        Assert.Equal(1, secrets.ProtectCallCount);
    }

    [Fact]
    public async Task IntakeFromOneTenantIsNeverVisibleAsCanonicalToAnotherTenant()
    {
        var scopeA = Scope();
        var scopeB = Scope();
        var waves = new FakeWaveStore();
        var waveA = SeedWave(waves, scopeA);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now));

        await useCase.ExecuteAsync(
            new IntakePurviewSasRequest(scopeA, waveA.Id, RedactedSecret.Wrap(ValidSasUri(scopeA)), CorrelationId.New()),
            CancellationToken.None);

        var fromOtherTenant = await handles.GetCanonicalAsync(scopeB, waveA.Id, CancellationToken.None);
        Assert.Null(fromOtherTenant);
    }

    // ---- AcquireSasForUploadUseCase ------------------------------------------------------------------

    [Fact]
    public async Task AcquireByTheUploadWorkerIdentityReturnsTheOriginalSecretAndConsumesTheHandle()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var rawSas = ValidSasUri(scope);
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(rawSas), CorrelationId.New()), CancellationToken.None);

        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now));
        var acquired = await useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(rawSas, acquired.Reveal());

        var handle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Consumed, handle!.State);
    }

    [Fact]
    public async Task AcquireByAnUnauthorizedIdentityIsDeniedAndNeverTouchesTheSecretStore()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now));
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, new WorkloadIdentity("SomeoneElse"), CorrelationId.New()), CancellationToken.None));

        Assert.Equal(0, secrets.AcquireCallCount);
        var handle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Available, handle!.State);
    }

    [Fact]
    public async Task AcquireWithNoHandleForTheWaveIsDenied()
    {
        var scope = Scope();
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now));

        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, WaveId.New(), WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task ASecondAcquireAttemptAfterConsumptionIsDenied()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now));
        await useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None);

        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));
        Assert.Equal(1, secrets.AcquireCallCount);
    }

    [Fact]
    public async Task AcquireAfterExpiryIsDeniedAndMarksTheHandleExpired()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(
                new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope, Now.AddMinutes(10))), CorrelationId.New()),
                CancellationToken.None);

        var afterExpiry = new StubClock(Now.AddHours(1));
        var useCase = new AcquireSasForUploadUseCase(handles, secrets, afterExpiry);
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        var handle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Expired, handle!.State);
        Assert.Equal(0, secrets.AcquireCallCount);
    }

    // ---- AcquireSasForUploadUseCase: claim/lease/fencing (AB-I5-006 item 2) --------------------------

    [Fact]
    public async Task TwoConcurrentClaimAttemptsNeverBothReceiveTheSecret()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        // Simula um segundo adquirente que vence a corrida de reivindicação exatamente entre a leitura do
        // canônico desta execução e a persistência da transição Available -> Claimed.
        var raced = false;
        handles.BeforeSaveTransitionAttempt = () =>
        {
            if (!raced)
            {
                raced = true;
                var canonical = handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None).GetAwaiter().GetResult();
                handles.SeedDirectly(canonical!.Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now));
            }
        };

        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now));
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        // A execução perdedora NUNCA chega a chamar o secret store — o "vencedor" simulado detém o claim.
        Assert.Equal(0, secrets.AcquireCallCount);
        var handle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, handle!.State);
    }

    [Fact]
    public async Task AcquireWhileAnotherClaimIsStillWithinItsLeaseIsDeniedWithoutTouchingTheSecretStore()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        var canonical = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        handles.SeedDirectly(canonical!.Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now)); // outro adquirente já detém o claim

        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now.AddMinutes(1))); // ainda dentro do lease
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        Assert.Equal(0, secrets.AcquireCallCount);
    }

    [Fact]
    public async Task AFailedSecretStoreReadAfterClaimingNeverBurnsTheGenerationAndIsRecoverableByReclaimAfterTheLeaseExpires()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var rawSas = ValidSasUri(scope);
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(rawSas), CorrelationId.New()), CancellationToken.None);

        var leaseDuration = TimeSpan.FromMinutes(5);
        secrets.FailNextAcquireWith = () => new SecretStoreUnavailableException("Falha simulada do secret store.");
        var firstAttempt = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now), leaseDuration);
        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => firstAttempt.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        // O claim permanece ATIVO (nunca finalizado nem revertido) — a geração nunca é queimada por uma
        // falha do secret store ANTES da leitura bem-sucedida (item 2).
        var afterFailure = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, afterFailure!.State);

        // Antes do lease expirar: recusado sem nova tentativa de leitura (claim ainda ativo).
        var stillLeased = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now.AddMinutes(1)), leaseDuration);
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => stillLeased.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        // Depois do lease expirar: reclaim recupera e entrega o segredo com sucesso — recuperável, não
        // readquirível permanentemente perdido.
        var afterLeaseExpiry = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now.AddMinutes(6)), leaseDuration);
        var acquired = await afterLeaseExpiry.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(rawSas, acquired.Reveal());
        var finalHandle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Consumed, finalHandle!.State);
    }

    [Fact]
    public async Task TheClaimLeaseNeverOutlivesTheSasExpiryEvenWithALongLeaseDuration()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(
                new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope, Now.AddMinutes(10))), CorrelationId.New()),
                CancellationToken.None);

        secrets.FailNextAcquireWith = () => new SecretStoreUnavailableException("Falha simulada do secret store.");
        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now), TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        var handle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, handle!.State);
        Assert.True(handle.ClaimExpiresAtUtc <= handle.ExpiresAtUtc);
    }

    // ---- AcquireSasForUploadUseCase: finalize fail-closed sob perda de fencing (AB-I5-007) -----------

    [Fact]
    public async Task AFinalizeClaimLostToAConcurrentReclaimNeverReturnsTheSecretToTheStaleClaimant()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        // Simula: entre a leitura bem-sucedida do secret store e a persistência de FinalizeClaim, o lease
        // titular expira e outro processo com a MESMA identidade autorizada já reivindicou por Reclaim —
        // rotacionando owner/época antes desta finalização tentar persistir.
        var saveAttempts = 0;
        handles.BeforeSaveTransitionAttempt = () =>
        {
            saveAttempts++;
            if (saveAttempts == 2) // a 1ª tentativa é o Claim inicial; a 2ª é sempre a finalização
            {
                var stolen = handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None).GetAwaiter().GetResult();
                handles.SeedDirectly(stolen!.Reclaim(WorkloadIdentities.UploadWorker, Now.AddMinutes(10), Now.AddMinutes(6)));
            }
        };

        var useCase = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now));
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        // O secret store FOI lido com sucesso pelo requester perdedor (o texto claro já tinha sido revelado
        // internamente) — mas ele NUNCA chega a sair do use case: fail-closed, sem entrega dupla observável.
        Assert.Equal(1, secrets.AcquireCallCount);
        var handle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, handle!.State); // permanece sob o owner/época do reclaim simulado, nunca Consumed
    }

    [Fact]
    public async Task OnlyTheClaimantThatPersistsConsumedReceivesTheSecret()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var rawSas = ValidSasUri(scope);
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(rawSas), CorrelationId.New()), CancellationToken.None);

        var saveAttempts = 0;
        handles.BeforeSaveTransitionAttempt = () =>
        {
            saveAttempts++;
            if (saveAttempts == 2)
            {
                var stolen = handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None).GetAwaiter().GetResult();
                handles.SeedDirectly(stolen!.Reclaim(WorkloadIdentities.UploadWorker, Now.AddMinutes(10), Now.AddMinutes(6)));
            }
        };

        var loser = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now));
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => loser.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        // Depois que o lease do "ladrão" simulado também expira, o próximo adquirente legítimo reclama e
        // finaliza com sucesso — é o ÚNICO que efetivamente sai do use case com o segredo em mãos.
        var winner = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now.AddMinutes(11)));
        var acquired = await winner.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(rawSas, acquired.Reveal());
        Assert.Equal(2, secrets.AcquireCallCount); // uma leitura pelo perdedor (nunca entregue) + uma pelo vencedor
        var handle = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Consumed, handle!.State);
    }

    [Fact]
    public async Task AStaleRowVersionAtFinalizeIsNeverTreatedAsASuccessfulDelivery()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        var available = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        var claimed = await handles.SaveTransitionAsync(
            available!.Claim(WorkloadIdentities.UploadWorker, Now.AddMinutes(5), Now), CancellationToken.None);

        // Outro processo reivindica novamente APÓS o lease expirar (avança o row_version/época) antes desta
        // finalização persistir — a versão de linha que 'claimed' carrega em mãos agora está obsoleta.
        await handles.SaveTransitionAsync(
            claimed.Reclaim(WorkloadIdentities.UploadWorker, Now.AddMinutes(20), Now.AddMinutes(6)), CancellationToken.None);

        var staleFinalize = claimed.FinalizeClaim(WorkloadIdentities.UploadWorker, claimed.ClaimEpoch, Now.AddMinutes(1));
        await Assert.ThrowsAsync<ConcurrencyException>(() => handles.SaveTransitionAsync(staleFinalize, CancellationToken.None));

        var current = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, current!.State); // nunca Consumed por uma finalização com row_version obsoleto
    }

    [Fact]
    public async Task CancellationOrFailureBeforeFinalizeRemainsRecoverableByReclaimWithoutDoubleDelivery()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var rawSas = ValidSasUri(scope);
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(rawSas), CorrelationId.New()), CancellationToken.None);

        var leaseDuration = TimeSpan.FromMinutes(5);
        secrets.FailNextAcquireWith = () => new OperationCanceledException("Cancelamento simulado antes da finalização.");
        var cancelledAttempt = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now), leaseDuration);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledAttempt.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        // Claim ainda ATIVO (nem finalizado nem liberado) — nunca queima a geração.
        var afterCancellation = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, afterCancellation!.State);

        // Depois do lease expirar: reclaim recupera e entrega o segredo com sucesso.
        var recovered = new AcquireSasForUploadUseCase(handles, secrets, new StubClock(Now.AddMinutes(6)), leaseDuration);
        var acquired = await recovered.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(rawSas, acquired.Reveal());

        // Nenhuma entrega dupla: uma nova tentativa sobre a mesma geração (já Consumed) é recusada.
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => recovered.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));
        Assert.Equal(2, secrets.AcquireCallCount);
    }

    [Fact]
    public void ConstructingWithANonPositiveClaimLeaseDurationThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AcquireSasForUploadUseCase(new FakeSasHandleStore(), new FakeSecretStore(), new StubClock(Now), TimeSpan.Zero));
    }

    // ---- DestroySasHandleUseCase --------------------------------------------------------------------

    [Fact]
    public async Task DestroyRemovesTheSecretMaterialAndMarksTheHandleDestroyed()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        var useCase = new DestroySasHandleUseCase(handles, secrets, new StubClock(Now));
        var result = await useCase.ExecuteAsync(new DestroySasHandleRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(SasHandleState.Destroyed, result.State);
        Assert.Equal(1, secrets.DestroyCallCount);
    }

    [Fact]
    public async Task DestroyIsIdempotentInResultButAlwaysRetriesTheSecretStoreDestroyForCrashSafety()
    {
        // AB-I5-006 item 3: o resultado (handle Destroyed) é idempotente, mas ISecretStore.DestroyAsync é
        // SEMPRE reexecutado — mesmo quando o metadado já estava Destroyed — para que uma tentativa
        // anterior que caiu ENTRE as duas etapas (metadado já Destroyed, material ainda não removido)
        // eventualmente convirja por retry, em vez de deixar o material para sempre. DestroyAsync já é
        // idempotente por si só (um segundo DELETE sobre uma referência já removida é um no-op).
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        var useCase = new DestroySasHandleUseCase(handles, secrets, new StubClock(Now));
        await useCase.ExecuteAsync(new DestroySasHandleRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);
        var second = await useCase.ExecuteAsync(new DestroySasHandleRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(SasHandleState.Destroyed, second.State);
        Assert.Equal(2, secrets.DestroyCallCount);
        Assert.Equal(0, secrets.MaterialCount);
    }

    [Fact]
    public async Task WhenTheSecretStoreDestroyFailsTheMetadataIsAlreadyDestroyedAndARetryConverges()
    {
        var scope = Scope();
        var waves = new FakeWaveStore();
        var wave = SeedWave(waves, scope);
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        await new IntakePurviewSasUseCase(waves, handles, secrets, new StubClock(Now))
            .ExecuteAsync(new IntakePurviewSasRequest(scope, wave.Id, RedactedSecret.Wrap(ValidSasUri(scope)), CorrelationId.New()), CancellationToken.None);

        secrets.FailNextDestroyWith = () => new SecretStoreUnavailableException("Falha simulada do secret store.");
        var useCase = new DestroySasHandleUseCase(handles, secrets, new StubClock(Now));
        await Assert.ThrowsAsync<SecretStoreUnavailableException>(
            () => useCase.ExecuteAsync(new DestroySasHandleRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None));

        // Metadado JÁ transicionou para Destroyed — inacessível a AcquireSasForUploadUseCase — mesmo com o
        // material ainda pendente de remoção (nunca "aparenta disponível apontando para material já apagado").
        var afterFailure = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Destroyed, afterFailure!.State);
        Assert.Equal(1, secrets.MaterialCount);

        // Retry converge: reexecuta SOMENTE a destruição do material (o metadado já Destroyed não transiciona de novo).
        var result = await useCase.ExecuteAsync(new DestroySasHandleRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(SasHandleState.Destroyed, result.State);
        Assert.Equal(0, secrets.MaterialCount);
    }

    [Fact]
    public async Task DestroyWithNoHandleForTheWaveIsDenied()
    {
        var scope = Scope();
        var handles = new FakeSasHandleStore();
        var secrets = new FakeSecretStore();
        var useCase = new DestroySasHandleUseCase(handles, secrets, new StubClock(Now));

        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new DestroySasHandleRequest(scope, WaveId.New(), CorrelationId.New()), CancellationToken.None));
    }
}

/// <summary>
/// Duplo de teste MÍNIMO da porta <see cref="ISecretStore"/> — em memória, sem DPAPI. Reproduz a
/// revalidação de identidade em profundidade que <c>DpapiSecretStore</c> também aplica.
/// </summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, (TenantScope Scope, RedactedSecret Secret)> _material = [];

    public int ProtectCallCount { get; private set; }

    public int AcquireCallCount { get; private set; }

    public int DestroyCallCount { get; private set; }

    /// <summary>Contagem de referências ainda "vivas" no material — usada para provar ausência de órfãos permanentes.</summary>
    public int MaterialCount => _material.Count;

    /// <summary>Hook de teste: quando definido, <see cref="AcquireAsync"/> lança este erro em vez de devolver o segredo (simula falha do secret store DEPOIS de um claim bem-sucedido).</summary>
    public Func<Exception>? FailNextAcquireWith { get; set; }

    /// <summary>Hook de teste: quando definido, <see cref="DestroyAsync"/> lança este erro em vez de remover o material (simula falha do secret store DEPOIS do metadado já ter transicionado para Destroyed).</summary>
    public Func<Exception>? FailNextDestroyWith { get; set; }

    public Task<SecretStoreHandleReference> ProtectAsync(
        TenantScope scope, RedactedSecret secret, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ProtectCallCount++;
        var reference = Guid.NewGuid().ToString("N");
        _material[reference] = (scope, secret);
        return Task.FromResult(new SecretStoreHandleReference(reference));
    }

    public Task<RedactedSecret> AcquireAsync(
        TenantScope scope, SecretStoreHandleReference reference, WorkloadIdentity requester, CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        AcquireCallCount++;
        if (FailNextAcquireWith is { } makeException)
        {
            FailNextAcquireWith = null;
            throw makeException();
        }

        if (!string.Equals(requester.Value, WorkloadIdentities.UploadWorker.Value, StringComparison.Ordinal))
        {
            throw new SecretStoreAccessDeniedException("Identidade não autorizada.");
        }

        if (!_material.TryGetValue(reference.Value, out var entry) || !entry.Scope.Equals(scope))
        {
            throw new SecretStoreAccessDeniedException("Referência inexistente/fora do escopo.");
        }

        return Task.FromResult(entry.Secret);
    }

    public Task DestroyAsync(TenantScope scope, SecretStoreHandleReference reference, CorrelationId correlation, CancellationToken cancellationToken)
    {
        DestroyCallCount++;
        if (FailNextDestroyWith is { } makeException)
        {
            FailNextDestroyWith = null;
            throw makeException();
        }

        _material.Remove(reference.Value);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Duplo de teste MÍNIMO da porta <see cref="IPurviewSasUploadHandleStore"/> — em memória, sem SQL.
/// Reproduz a semântica de canonicidade/concorrência exigida (item 15/16): no máximo um handle "vivo"
/// por wave; um replace destrói o anterior; a transição usa <see cref="RowVersion"/> como token otimista.
/// </summary>
internal sealed class FakeSasHandleStore : IPurviewSasUploadHandleStore
{
    private readonly Dictionary<Guid, PurviewSasUploadHandle> _handles = [];
    private readonly Dictionary<Guid, ulong> _rowVersions = [];

    /// <summary>Hook de teste: invocado imediatamente antes de CADA tentativa de <see cref="ReplaceCanonicalAsync"/>.</summary>
    public Action? BeforeReplaceAttempt { get; set; }

    /// <summary>Hook de teste: invocado imediatamente antes de CADA tentativa de <see cref="SaveTransitionAsync"/>.</summary>
    public Action? BeforeSaveTransitionAttempt { get; set; }

    public void SeedDirectly(PurviewSasUploadHandle handle) => Store(handle);

    public Task<PurviewSasUploadHandle?> GetCanonicalAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken)
    {
        var latest = _handles.Values
            .Where(h => h.Tenant == scope.Tenant && h.Project == scope.Project && h.Wave == wave)
            .OrderByDescending(h => h.Generation)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<PurviewSasUploadHandle?> GetByIdAsync(TenantScope scope, SasHandleId id, CancellationToken cancellationToken)
    {
        if (_handles.TryGetValue(id.Value, out var handle) && handle.Tenant == scope.Tenant && handle.Project == scope.Project)
        {
            return Task.FromResult<PurviewSasUploadHandle?>(handle);
        }

        return Task.FromResult<PurviewSasUploadHandle?>(null);
    }

    public Task<PurviewSasUploadHandle> ReplaceCanonicalAsync(
        TenantScope scope, WaveId wave, PurviewSasUploadHandle? expectedPrevious, PurviewSasUploadHandle candidate,
        CancellationToken cancellationToken)
    {
        BeforeReplaceAttempt?.Invoke();

        // Estados "vivos" (nunca dois simultâneos por wave) — espelha o índice único filtrado
        // UX_psuh_canonical_live da migration 0027 (Stored/Available/Claimed/Consumed).
        var live = _handles.Values.SingleOrDefault(h =>
            h.Tenant == scope.Tenant && h.Project == scope.Project && h.Wave == wave
            && h.State is SasHandleState.Stored or SasHandleState.Available or SasHandleState.Claimed or SasHandleState.Consumed);

        if (expectedPrevious is null)
        {
            if (live is not null)
            {
                throw new ConcurrencyException("Já existe um handle vivo para esta wave (corrida de intake).");
            }
        }
        else
        {
            if (live is null || live.Id != expectedPrevious.Id || _rowVersions[live.Id.Value] != expectedPrevious.RowVersion.Value)
            {
                throw new ConcurrencyException("O handle canônico mudou concorrentemente antes da substituição.");
            }

            Store(live.Destroy(candidate.StoredAtUtc));
        }

        Store(candidate);
        return Task.FromResult(_handles[candidate.Id.Value]);
    }

    public Task<PurviewSasUploadHandle> SaveTransitionAsync(PurviewSasUploadHandle handle, CancellationToken cancellationToken)
    {
        BeforeSaveTransitionAttempt?.Invoke();

        if (!_rowVersions.TryGetValue(handle.Id.Value, out var current) || current != handle.RowVersion.Value)
        {
            throw new ConcurrencyException($"Handle {handle.Id.Value}: row_version divergente.");
        }

        Store(handle);
        return Task.FromResult(_handles[handle.Id.Value]);
    }

    private void Store(PurviewSasUploadHandle handle)
    {
        var nextRowVersion = _rowVersions.TryGetValue(handle.Id.Value, out var current) ? current + 1 : 1;
        _rowVersions[handle.Id.Value] = nextRowVersion;
        _handles[handle.Id.Value] = PurviewSasUploadHandle.Rehydrate(
            handle.Id, handle.Tenant, handle.Project, handle.Wave, handle.Generation, handle.State, handle.Fingerprint,
            handle.SecretStoreReference, handle.AuthorizedHost, handle.AuthorizedContainer, handle.KeyVersion,
            handle.ExpiresAtUtc, handle.StoredAtUtc, handle.AvailableAtUtc, handle.ConsumedAtUtc, handle.ExpiredAtUtc,
            handle.DestroyedAtUtc, handle.ClaimOwner, handle.ClaimEpoch, handle.ClaimExpiresAtUtc, handle.Correlation,
            handle.RecordedAtUtc, new RowVersion(nextRowVersion), handle.HandleHash);
    }
}
