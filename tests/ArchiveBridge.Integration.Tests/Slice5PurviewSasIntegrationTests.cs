using System.Data;
using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// I5/EPIC-06 Passo 2 (AB-I5-004) sob SQL Server real: persistência do handle opaco, canonicidade sob
/// corrida (índice único filtrado), concorrência otimista de transição (row_version), isolamento por
/// tenant/projeto (RLS), fronteira NÃO CONFIÁVEL de <see cref="PurviewSasUploadHandle.Rehydrate"/> e o
/// adapter DPAPI (round-trip quando suportado; falha segura quando não).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice5PurviewSasIntegrationTests(SqlServerFixture fixture)
{
    private SqlPurviewSasUploadHandleStore HandleStore => new(fixture.Factory, new MutableClock(Slice2Support.Now));

    private SqlWaveStore WaveStore => Slice2Support.WaveStore(fixture);

    private async Task<MigrationWave> SeedWaveAsync(TenantScope scope, string mailbox = "user01@contoso.com")
    {
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var selection = new WaveSelection([Slice2Support.Entry($"{mailbox}.pst", mailbox, 1_000_000_000)]);
        var wave = Slice2Support.NewWave(scope, selection);
        await WaveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return wave;
    }

    private static PurviewSasUploadHandle NewHandle(TenantScope scope, WaveId wave, int generation, DateTimeOffset now) =>
        PurviewSasUploadHandle.Intake(
            SasHandleId.New(), scope.Tenant, scope.Project, wave, generation, new Sha256Hash(new string('a', 64)),
            new SecretStoreHandleReference($"ref-{Guid.NewGuid():N}"), "mystorageaccount123.blob.core.windows.net",
            "ingestiondata", null, now.AddHours(2), CorrelationId.New(), now);

    // ---- Persistência, canonicidade e versionamento ---------------------------------------------------

    [Fact]
    public async Task InsertingTheFirstHandleForAWavePersistsItAsCanonical()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var candidate = NewHandle(scope, wave.Id, 1, Slice2Support.Now);

        var inserted = await HandleStore.ReplaceCanonicalAsync(scope, wave.Id, null, candidate, CancellationToken.None);
        Assert.True(inserted.RowVersion.IsPersisted);

        var canonical = await HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(candidate.Id, canonical!.Id);
        Assert.Equal(SasHandleState.Stored, canonical.State);
    }

    [Fact]
    public async Task ReplacingDestroysThePreviousGenerationAndInsertsTheNewOneAtomically()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var first = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var second = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, first, NewHandle(scope, wave.Id, 2, Slice2Support.Now), CancellationToken.None);

        var canonical = await HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(second.Id, canonical!.Id);
        Assert.Equal(2, canonical.Generation);

        var previous = await HandleStore.GetByIdAsync(scope, first.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Destroyed, previous!.State);
        Assert.NotNull(previous.DestroyedAtUtc);
    }

    [Fact]
    public async Task ConcurrentFirstIntakeForTheSameWaveNeverProducesTwoLiveCanonicalHandles()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);

        // Segunda tentativa de PRIMEIRO intake (expectedPrevious=null) para a MESMA wave — o índice único
        // filtrado UX_psuh_canonical_live é o backstop: nunca dois handles "vivos" simultaneamente (item 16).
        await Assert.ThrowsAsync<ConcurrencyException>(() => HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None));

        var canonical = await HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(1, canonical!.Generation);
    }

    [Fact]
    public async Task ReplacingWithAStaleExpectedPreviousFailsClosed()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var first = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        // Consome o row_version real fazendo uma transição — a instância 'first' em mãos do teste fica STALE.
        await HandleStore.SaveTransitionAsync(first.MarkAvailable(Slice2Support.Now), CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyException>(() => HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, first, NewHandle(scope, wave.Id, 2, Slice2Support.Now), CancellationToken.None));
    }

    // ---- Transição de ciclo de vida (concorrência otimista) --------------------------------------------

    [Fact]
    public async Task SaveTransitionPersistsTheLifecycleAcrossReads()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);

        var available = await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);
        var reloaded = await HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);

        Assert.Equal(SasHandleState.Available, reloaded!.State);
        Assert.NotNull(reloaded.AvailableAtUtc);
        Assert.Equal(available.HandleHash.Value, reloaded.HandleHash.Value);
    }

    [Fact]
    public async Task SaveTransitionWithAStaleRowVersionFailsClosed()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);

        // 'stored' está STALE — o row_version já avançou pela transição acima.
        await Assert.ThrowsAsync<ConcurrencyException>(
            () => HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None));
    }

    // ---- Claim / Reclaim / FinalizeClaim sob SQL real (AB-I5-006 item 2) -------------------------------

    [Fact]
    public async Task ClaimTransitionPersistsAcrossReads()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var available = await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);

        var claimed = await HandleStore.SaveTransitionAsync(
            available.Claim(WorkloadIdentities.UploadWorker, Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None);
        var reloaded = await HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);

        Assert.Equal(SasHandleState.Claimed, reloaded!.State);
        Assert.Equal(WorkloadIdentities.UploadWorker, reloaded.ClaimOwner);
        Assert.Equal(1, reloaded.ClaimEpoch.Value);
        Assert.NotNull(reloaded.ClaimExpiresAtUtc);
        Assert.Equal(claimed.HandleHash.Value, reloaded.HandleHash.Value);
    }

    [Fact]
    public async Task ReclaimAfterLeaseExpiryPersistsAndRotatesTheEpochAndOwner()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var clock = new MutableClock(Slice2Support.Now);
        var store = new SqlPurviewSasUploadHandleStore(fixture.Factory, clock);

        var stored = await store.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var available = await store.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);
        var claimed = await store.SaveTransitionAsync(
            available.Claim(new WorkloadIdentity("OldOwner"), Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None);

        clock.Set(Slice2Support.Now.AddMinutes(6)); // depois do lease expirar
        var reclaimed = await store.SaveTransitionAsync(
            claimed.Reclaim(new WorkloadIdentity("NewOwner"), clock.UtcNow.AddMinutes(5), clock.UtcNow), CancellationToken.None);

        var reloaded = await store.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, reloaded!.State);
        Assert.Equal(new WorkloadIdentity("NewOwner"), reloaded.ClaimOwner);
        Assert.Equal(2, reloaded.ClaimEpoch.Value);
        Assert.Equal(reclaimed.HandleHash.Value, reloaded.HandleHash.Value);

        // O owner ANTERIOR nunca mais finaliza com a época antiga (fencing) — domínio puro, mas provado
        // aqui contra o handle efetivamente reidratado do SQL (nunca a instância em memória do teste).
        Assert.Throws<PurviewSasLifecycleException>(
            () => reloaded.FinalizeClaim(new WorkloadIdentity("OldOwner"), new LeaseEpoch(1), clock.UtcNow));
    }

    [Fact]
    public async Task FinalizeClaimAfterAcquisitionPersistsAsConsumed()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var available = await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);
        var claimed = await HandleStore.SaveTransitionAsync(
            available.Claim(WorkloadIdentities.UploadWorker, Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None);

        var consumed = await HandleStore.SaveTransitionAsync(
            claimed.FinalizeClaim(WorkloadIdentities.UploadWorker, claimed.ClaimEpoch, Slice2Support.Now.AddMinutes(1)), CancellationToken.None);
        var reloaded = await HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);

        Assert.Equal(SasHandleState.Consumed, reloaded!.State);
        Assert.NotNull(reloaded.ConsumedAtUtc);
        Assert.Equal(consumed.HandleHash.Value, reloaded.HandleHash.Value);
    }

    [Fact]
    public async Task ClaimWithAStaleRowVersionFailsClosed()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var available = await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);

        // Consome o row_version real com um claim bem-sucedido — a instância 'available' em mãos do teste
        // fica STALE, simulando um segundo adquirente perdendo a corrida.
        await HandleStore.SaveTransitionAsync(
            available.Claim(WorkloadIdentities.UploadWorker, Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyException>(() => HandleStore.SaveTransitionAsync(
            available.Claim(new WorkloadIdentity("AnotherWorker"), Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeClaimLostToAConcurrentReclaimFailsClosedUnderRealConcurrency()
    {
        // AB-I5-007: sob concorrência genuína, a finalização do titular original NUNCA pode ser tratada
        // como sucesso depois que outro processo já reivindicou o handle por Reclaim (row_version/época
        // rotacionados) — mesmo que o titular original já tenha lido o segredo com sucesso antes disso.
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var available = await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);
        var claimed = await HandleStore.SaveTransitionAsync(
            available.Claim(WorkloadIdentities.UploadWorker, Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None);

        var clock = new MutableClock(Slice2Support.Now.AddMinutes(6)); // depois do lease titular expirar
        var store = new SqlPurviewSasUploadHandleStore(fixture.Factory, clock);

        // Outro processo reivindica por Reclaim (row_version avança) ANTES desta finalização persistir.
        await store.SaveTransitionAsync(
            claimed.Reclaim(new WorkloadIdentity("OtherWorker"), clock.UtcNow.AddMinutes(5), clock.UtcNow), CancellationToken.None);

        // 'claimed' está STALE. Usamos um 'nowUtc' de finalização AINDA dentro do lease ORIGINAL (item 2 do
        // titular original, AB-I5-008) para isolar esta corrida da checagem temporal adicionada em
        // AB-I5-008 (coberta separadamente por FinalizeClaimTemporallyExpiredNeverPersistsConsumedEvenWithAFreshRowVersion
        // abaixo): mesmo com uma visão local ainda temporalmente válida, o row_version obsoleto no SQL real
        // (por causa do Reclaim concorrente) é o que faz esta finalização falhar fechado.
        await Assert.ThrowsAsync<ConcurrencyException>(() => store.SaveTransitionAsync(
            claimed.FinalizeClaim(WorkloadIdentities.UploadWorker, claimed.ClaimEpoch, Slice2Support.Now.AddMinutes(1)),
            CancellationToken.None));

        var reloaded = await store.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, reloaded!.State);
        Assert.Equal(new WorkloadIdentity("OtherWorker"), reloaded.ClaimOwner);
    }

    [Fact]
    public async Task ATemporallyExpiredFinalizeNeverPersistsConsumedEvenWithAFreshRowVersion()
    {
        // AB-I5-008: nenhum reclaim concorrente ocorre aqui — o row_version permanece o mesmo que 'claimed'
        // já carrega. Ainda assim, a finalização deve recusar fail-closed quando o lease de claim já expirou
        // no instante informado, e a rejeição deve ocorrer ANTES de qualquer tentativa de persistência (a
        // exceção nasce em PurviewSasUploadHandle.FinalizeClaim, no Domain — nunca chega ao SQL).
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var available = await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);
        var claimed = await HandleStore.SaveTransitionAsync(
            available.Claim(WorkloadIdentities.UploadWorker, Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None);

        Assert.Throws<PurviewSasLifecycleException>(
            () => claimed.FinalizeClaim(WorkloadIdentities.UploadWorker, claimed.ClaimEpoch, Slice2Support.Now.AddMinutes(6)));

        // O row_version de 'claimed' nunca é consumido pela tentativa recusada — o handle persistido
        // continua exatamente como estava (Claimed, mesmo owner/época), pronto para um Reclaim futuro.
        var reloaded = await HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, reloaded!.State);
        Assert.Equal(WorkloadIdentities.UploadWorker, reloaded.ClaimOwner);
        Assert.Equal(claimed.ClaimEpoch, reloaded.ClaimEpoch);
        Assert.Equal(claimed.RowVersion.Value, reloaded.RowVersion.Value);

        // Um Reclaim subsequente (nova geração de owner) continua funcionando normalmente sobre o MESMO
        // row_version — a rejeição temporal não deixou nenhum resíduo no SQL.
        var reclaimed = await HandleStore.SaveTransitionAsync(
            reloaded.Reclaim(new WorkloadIdentity("NewOwner"), Slice2Support.Now.AddMinutes(11), Slice2Support.Now.AddMinutes(6)),
            CancellationToken.None);
        Assert.Equal(SasHandleState.Claimed, reclaimed.State);
        Assert.Equal(new WorkloadIdentity("NewOwner"), reclaimed.ClaimOwner);
    }

    [Fact]
    public async Task GetCanonicalFailsClosedWhenTheClaimOwnerIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);
        var available = await HandleStore.SaveTransitionAsync(stored.MarkAvailable(Slice2Support.Now), CancellationToken.None);
        await HandleStore.SaveTransitionAsync(
            available.Claim(WorkloadIdentities.UploadWorker, Slice2Support.Now.AddMinutes(5), Slice2Support.Now), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.purview_sas_upload_handles SET claim_owner = N'AttackerControlled' WHERE handle_id = @id;",
            ("@id", stored.Id.Value));

        await Assert.ThrowsAsync<PurviewSasHandleIntegrityViolationException>(
            () => HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None));
    }

    // ---- Isolamento cross-tenant/project (RLS) e fronteira NÃO CONFIÁVEL -------------------------------

    [Fact]
    public async Task HandleFromAnotherProjectIsIndistinguishableFromNotFound()
    {
        var scopeA = SqlServerFixture.NewScope();
        var scopeB = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scopeA);
        await HandleStore.ReplaceCanonicalAsync(
            scopeA, wave.Id, null, NewHandle(scopeA, wave.Id, 1, Slice2Support.Now), CancellationToken.None);

        var fromOtherScope = await HandleStore.GetCanonicalAsync(scopeB, wave.Id, CancellationToken.None);
        Assert.Null(fromOtherScope);
    }

    [Fact]
    public async Task GetCanonicalFailsClosedWhenTheHandleHashIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = await SeedWaveAsync(scope);
        var stored = await HandleStore.ReplaceCanonicalAsync(
            scope, wave.Id, null, NewHandle(scope, wave.Id, 1, Slice2Support.Now), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.purview_sas_upload_handles SET state = 1 WHERE handle_id = @id;", // Stored(0) forjado para Available(1)
            ("@id", stored.Id.Value));

        await Assert.ThrowsAsync<PurviewSasHandleIntegrityViolationException>(
            () => HandleStore.GetCanonicalAsync(scope, wave.Id, CancellationToken.None));
    }

    // ---- Migration/gates --------------------------------------------------------------------------------

    [Fact]
    public async Task Migration0027AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0026 tivesse divergido, isto lançaria. Em seguida confirmamos a 0027 e as duas tabelas
        // novas da custódia de SAS (Passo 2).
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand("SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 27;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var tables = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name IN ('purview_sas_upload_handles', 'purview_sas_secret_material');",
            connection))
        {
            Assert.Equal(2, Convert.ToInt32(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Backstop de canonicidade (item 16): índice único FILTRADO sobre os estados "vivos" — inclui
        // Claimed(5) desde AB-I5-006 item 2.
        await using (var canonicalIndex = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_psuh_canonical_live' AND has_filter = 1;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await canonicalIndex.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Estado permite até Claimed(5); consistência claim_epoch/claim_owner/claim_expires_at_utc reforçada
        // no BANCO (defesa em profundidade da mesma regra do Domain, AB-I5-006 item 2).
        await using (var stateCheck = new SqlCommand(
            "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_psuh_state' AND definition LIKE '%5%';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await stateCheck.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var claimConsistencyCheck = new SqlCommand(
            "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_psuh_claim_consistency';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await claimConsistencyCheck.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // As três colunas de claim/lease/fencing existem na tabela de metadado.
        await using (var claimColumns = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.purview_sas_upload_handles')
              AND name IN ('claim_owner', 'claim_epoch', 'claim_expires_at_utc');
            """,
            connection))
        {
            Assert.Equal(3, Convert.ToInt32(await claimColumns.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Nenhum GRANT de leitura à identidade de MANUTENÇÃO em purview_sas_secret_material — nem sequer o
        // ciphertext protegido por DPAPI é alcançável por essa identidade (defesa em profundidade extra
        // além do padrão append-only já usado nas demais tabelas do release).
        await using var maintenanceGrants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_maintenance_role' AND o.name = 'purview_sas_secret_material';
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await maintenanceGrants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

        // purview_sas_upload_handles: a aplicação grava e ATUALIZA (transição de ciclo de vida), nunca DELETE.
        await using var handleGrants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role' AND o.name = 'purview_sas_upload_handles'
              AND p.permission_name NOT IN ('SELECT', 'INSERT', 'UPDATE');
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await handleGrants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    // ---- DpapiSecretStore: round-trip quando suportado, falha segura quando não -----------------------
    //
    // DpapiSecretStore é [SupportedOSPlatform("windows")] — o CI deste repositório roda em ubuntu-latest,
    // então o ramo executado aqui é SEMPRE o de falha segura (RequireDpapiAvailable) nesta pipeline. Os
    // dois testes abaixo permanecem corretos e válidos também sob Windows real (round-trip completo). O
    // analisador de compatibilidade de plataforma (CA1416) não enxerga o guard de runtime que o PRÓPRIO
    // DpapiSecretStore aplica internamente (RequireDpapiAvailable) antes de qualquer chamada real à API do
    // SO — a supressão abaixo é intencional e documentada, não um bypass de segurança.
#pragma warning disable CA1416

    [Fact]
    public async Task DpapiSecretStoreRoundTripsWhenSupportedAndFailsSafeOtherwise()
    {
        var scope = SqlServerFixture.NewScope();
        var store = new DpapiSecretStore(fixture.Factory, new MutableClock(Slice2Support.Now));
        var secret = RedactedSecret.Wrap(
            "https://mystorageaccount123.blob.core.windows.net/ingestiondata?sv=2022-11-02&se=2026-08-24T12%3A00%3A00Z&sp=cw&sig=abc");

        if (!OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<SecretStoreUnavailableException>(
                () => store.ProtectAsync(scope, secret, CorrelationId.New(), CancellationToken.None));
            return;
        }

        var reference = await store.ProtectAsync(scope, secret, CorrelationId.New(), CancellationToken.None);
        var acquired = await store.AcquireAsync(
            scope, reference, WorkloadIdentities.UploadWorker, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(secret.Reveal(), acquired.Reveal());

        await store.DestroyAsync(scope, reference, CorrelationId.New(), CancellationToken.None);
        await Assert.ThrowsAsync<SecretStoreAccessDeniedException>(() => store.AcquireAsync(
            scope, reference, WorkloadIdentities.UploadWorker, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task DpapiSecretStoreDeniesAcquisitionByAnUnauthorizedIdentityEvenWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Coberto pelo teste de falha segura acima quando DPAPI não está disponível neste SO.
        }

        var scope = SqlServerFixture.NewScope();
        var store = new DpapiSecretStore(fixture.Factory, new MutableClock(Slice2Support.Now));
        var reference = await store.ProtectAsync(
            scope, RedactedSecret.Wrap("https://mystorageaccount123.blob.core.windows.net/ingestiondata?sv=x&se=y&sp=cw&sig=z"),
            CorrelationId.New(), CancellationToken.None);

        await Assert.ThrowsAsync<SecretStoreAccessDeniedException>(() => store.AcquireAsync(
            scope, reference, new WorkloadIdentity("SomeoneElse"), CorrelationId.New(), CancellationToken.None));
    }

#pragma warning restore CA1416

    // ---- Helpers --------------------------------------------------------------------------------------

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
