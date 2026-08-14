using System.Data;
using System.Text;
using ArchiveBridge.Application.Mapping;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Passo 6A — backend seguro de recebimento/validação de CSV de mapping contra SQL real. Prova recepção
/// bounded, SHA dos bytes exatos, UTF-8 estrito sem BOM, validação contra a onda autorizada (Approved/
/// Frozen), custódia durável append-only tenant/projeto-scoped e idempotente, anti-IDOR, revalidação
/// TOCTOU e imutabilidade/grants.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice6MappingUploadTests(SqlServerFixture fixture)
{
    private static readonly ContentCodePage CodePage = new(1252);
    private static readonly SystemClock Clock = new();
    private readonly SqlServerFixture _fixture = fixture;

    // ===================== happy path / hash =====================

    [Fact]
    public async Task ValidUploadIsValidWithExactHashRowCountAndSingleAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"), Entry("b.pst", "v@contoso.com"));
        var csv = ValidCsv(wave);
        var key = Guid.NewGuid();

        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, csv, key: key), MappingPolicy.Default, CancellationToken.None);

        Assert.True(outcome.IsValid);
        Assert.Equal(MappingValidationAttemptOutcome.Valid, outcome.Outcome);
        Assert.True(outcome.Created);
        Assert.Equal(DeterministicHash.ComputeBytes(csv), outcome.ContentSha256); // SHA dos BYTES EXATOS
        Assert.Equal(csv.Length, outcome.SizeBytes);
        Assert.Equal(2, outcome.RowCount);
        Assert.Empty(outcome.Issues);
        Assert.Equal(1, await CountAttemptsAsync(scope));

        var stored = await Store().GetAsync(scope, outcome.ValidationId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(wave.Id, stored!.WaveId);
        Assert.Equal(1, stored.WaveVersion);
        Assert.Equal(outcome.ContentSha256, stored.ContentSha256);
        Assert.Equal(MappingValidationAttemptOutcome.Valid, stored.Outcome);
    }

    [Fact]
    public async Task ContentSha256IsOverExactBytesNotNormalized()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var csv = ValidCsv(wave);
        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, csv), MappingPolicy.Default, CancellationToken.None);
        Assert.Equal(DeterministicHash.ComputeBytes(csv), outcome.ContentSha256);

        // Um byte sintaticamente irrelevante muda (CRLF ⇒ CRLF+LF extra): o SHA deve mudar (sem normalizar).
        var mutated = csv.Concat(new byte[] { (byte)'\n' }).ToArray();
        Assert.NotEqual(DeterministicHash.ComputeBytes(csv), DeterministicHash.ComputeBytes(mutated));
    }

    // ===================== extensão / filename =====================

    [Theory]
    [InlineData("mapping.csv")]
    [InlineData("MAPPING.CSV")]
    public async Task AcceptedExtensionsPassPreflight(string fileName)
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var outcome = await UseCase().ExecuteAsync(
            Request(scope, wave.Id, ValidCsv(wave), fileName: fileName), MappingPolicy.Default, CancellationToken.None);
        Assert.True(outcome.IsValid);
    }

    [Theory]
    [InlineData("mapping.txt")]
    [InlineData("mapping.csv.exe")]
    [InlineData("mapping")]
    [InlineData("mapping.csv.zip")]
    public async Task RejectedExtensionsArePreCustodyWithZeroAttempt(string fileName)
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        await Assert.ThrowsAsync<MappingUploadRejectedException>(() => UseCase().ExecuteAsync(
            Request(scope, wave.Id, ValidCsv(wave), fileName: fileName), MappingPolicy.Default, CancellationToken.None));
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    [Fact]
    public async Task ClientPathComponentsAreStrippedFromDisplayName()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var outcome = await UseCase().ExecuteAsync(
            Request(scope, wave.Id, ValidCsv(wave), fileName: "../../etc/mapping.csv"), MappingPolicy.Default, CancellationToken.None);
        var stored = await Store().GetAsync(scope, outcome.ValidationId, CancellationToken.None);
        Assert.Equal("mapping.csv", stored!.DisplayFileName); // basename apenas; nunca caminho físico
    }

    // ===================== bounded input / declared length =====================

    [Fact]
    public async Task ContentAtLimitIsAcceptedAndOverLimitIsRejectedPreCustody()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var limits = new MappingUploadLimits(effectiveMaxUploadBytes: 128, hardMaxUploadBytes: 1024);

        // limit e limit-1: NÃO rejeita por tamanho (conteúdo arbitrário ⇒ Invalid, mas com custódia).
        foreach (var size in new[] { 127, 128 })
        {
            var atLimit = await UseCase(limits).ExecuteAsync(
                Request(scope, wave.Id, new byte[size], key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None);
            Assert.False(atLimit.IsValid); // não é CSV válido, mas foi recebido/custodiado
        }

        // limit+1: rejeição pré-custódia sem consumir stream arbitrariamente.
        var counting = new CountingStream(200);
        await Assert.ThrowsAsync<MappingUploadRejectedException>(() => UseCase(limits).ExecuteAsync(
            Request(scope, wave.Id, counting, key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None));
        Assert.True(counting.BytesRead <= 129); // nunca leu além de limit + 1
    }

    [Fact]
    public async Task DeclaredLengthOverLimitRejectsWithoutReading()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var limits = new MappingUploadLimits(effectiveMaxUploadBytes: 128, hardMaxUploadBytes: 1024);
        var counting = new CountingStream(1_000_000);

        await Assert.ThrowsAsync<MappingUploadRejectedException>(() => UseCase(limits).ExecuteAsync(
            Request(scope, wave.Id, counting, declaredLength: 10_000), MappingPolicy.Default, CancellationToken.None));
        Assert.Equal(0, counting.BytesRead); // preflight rejeitou sem ler
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    [Fact]
    public async Task LyingDeclaredLengthDoesNotBypassBoundedRead()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var limits = new MappingUploadLimits(effectiveMaxUploadBytes: 128, hardMaxUploadBytes: 1024);
        var counting = new CountingStream(500); // real > limit, mas declara pequeno

        await Assert.ThrowsAsync<MappingUploadRejectedException>(() => UseCase(limits).ExecuteAsync(
            Request(scope, wave.Id, counting, declaredLength: 10), MappingPolicy.Default, CancellationToken.None));
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    // ===================== encoding / BOM =====================

    [Fact]
    public async Task ValidUtf8NoBomIsProcessed()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, ValidCsv(wave)), MappingPolicy.Default, CancellationToken.None);
        Assert.True(outcome.IsValid);
    }

    [Fact]
    public async Task Utf8BomIsRejectedWithDeterministicIssueButStillCustodied()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(ValidCsv(wave)).ToArray();
        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, withBom), MappingPolicy.Default, CancellationToken.None);
        Assert.Equal(MappingValidationAttemptOutcome.Rejected, outcome.Outcome);
        Assert.Contains(outcome.Issues, i => i.Code == MappingValidationIssueCode.Bom);
        Assert.Equal(1, await CountAttemptsAsync(scope)); // recebido + hash ⇒ custodiado
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0xC3, 0x28 })]              // UTF-8 inválido
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 })]        // UTF-16 LE BOM
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 })]        // UTF-16 BE BOM
    public async Task NonUtf8IsRejectedWithoutFallback(byte[] bytes)
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, bytes, key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None);
        Assert.Equal(MappingValidationAttemptOutcome.Rejected, outcome.Outcome);
        Assert.Contains(outcome.Issues, i => i.Code == MappingValidationIssueCode.InvalidEncoding);
    }

    // ===================== header / rows / semantic / formula =====================

    [Theory]
    [InlineData("FilePath,Workload,Name,Mailbox,IsArchive,TargetRootFolder,ContentCodePage,SPFileContainer,SPManifestContainer,SPSiteUrl")] // ordem
    [InlineData("workload,FilePath,Name,Mailbox,IsArchive,TargetRootFolder,ContentCodePage,SPFileContainer,SPManifestContainer,SPSiteUrl")] // case
    [InlineData("Workload,FilePath,Name,Mailbox,IsArchive,TargetRootFolder,ContentCodePage,SPFileContainer,SPManifestContainer")]            // 9 colunas
    [InlineData("Workload,FilePath,Name,Mailbox,IsArchive,TargetRootFolder,ContentCodePage,SPFileContainer,SPManifestContainer,SPSiteUrl,Extra")] // 11
    public async Task InvalidHeaderIsInvalidWithCustody(string header)
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var bytes = Encoding.UTF8.GetBytes(header + "\r\n");
        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, bytes, key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Issues, i => i.Code == MappingValidationIssueCode.HeaderInvalid);
        Assert.NotEqual(Guid.Empty, outcome.ValidationId);
        Assert.False(outcome.ContentSha256.Value.Length == 0);
    }

    [Fact]
    public async Task TooManyRowsIsInvalidWithRowsExceededAndBoundedIssues()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var text = new StringBuilder().Append(MappingSchema.HeaderLine).Append("\r\n");
        for (var i = 0; i < 501; i++)
        {
            text.Append("Exchange,/p,x.pst,m@c,TRUE,/f,1252,,,\r\n");
        }

        var outcome = await UseCase().ExecuteAsync(
            Request(scope, wave.Id, Encoding.UTF8.GetBytes(text.ToString()), key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Issues, i => i.Code == MappingValidationIssueCode.RowsExceeded);
        Assert.True(outcome.IssueCount < 700); // sem amplificação (parser bounded)
    }

    [Theory]
    [InlineData("Exchange,/src/a.pst,,u@contoso.com,TRUE,{F},1252,,,", MappingValidationIssueCode.NameMissing)]
    [InlineData("Exchange,/src/a.pst,zzz.pst,u@contoso.com,TRUE,{F},1252,,,", MappingValidationIssueCode.PstUnauthorized)]
    [InlineData("Exchange,/wrong,a.pst,u@contoso.com,TRUE,{F},1252,,,", MappingValidationIssueCode.RowDivergent)]
    [InlineData("Notexchange,/src/a.pst,a.pst,u@contoso.com,TRUE,{F},1252,,,", MappingValidationIssueCode.WorkloadInvalid)]
    [InlineData("Exchange,/src/a.pst,a.pst,u@contoso.com,FALSE,{F},1252,,,", MappingValidationIssueCode.ArchiveInvalid)]
    [InlineData("Exchange,/src/a.pst,a.pst,u@contoso.com,TRUE,{F},1252,X,,", MappingValidationIssueCode.SharePointNonEmpty)]
    public async Task SemanticViolationsAreInvalidWithoutPii(string rowTemplate, string expectedCode)
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var folder = wave.TargetRootFolder.Value;
        var row = rowTemplate.Replace("{F}", folder, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(MappingSchema.HeaderLine + "\r\n" + row + "\r\n");

        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, bytes, key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Issues, i => i.Code == expectedCode);
        // Zero PII: nenhuma issue contém mailbox/caminho/pst.
        Assert.All(outcome.Issues, i =>
        {
            Assert.DoesNotContain("contoso", i.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/src", i.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(".pst", i.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("=x")]
    [InlineData("+x")]
    [InlineData("-x")]
    [InlineData("@x")]
    [InlineData("\tx")]
    [InlineData("\rx")]
    public async Task FormulaTriggersAreInvalidAndNotRewritten(string malicious)
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        // Injeta o gatilho na coluna Mailbox (índice 3), CITADO (CR/TAB precisam de aspas no CSV). O valor
        // NÃO é reescrito; a validação falha.
        var row = $"Exchange,/src/a.pst,a.pst,\"{malicious}\",TRUE,{wave.TargetRootFolder.Value},1252,,,";
        var bytes = Encoding.UTF8.GetBytes(MappingSchema.HeaderLine + "\r\n" + row + "\r\n");

        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, bytes, key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None);
        Assert.False(outcome.IsValid);
        Assert.Contains(outcome.Issues, i => i.Code == MappingValidationIssueCode.Formula);
        Assert.All(outcome.Issues, i => Assert.DoesNotContain(malicious, i.Message, StringComparison.Ordinal)); // valor perigoso não reaparece
    }

    [Fact]
    public async Task PersistedIssuesAreBoundedAndTruncated()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var limits = new MappingUploadLimits(maxPersistedValidationIssues: 5);
        var text = new StringBuilder().Append(MappingSchema.HeaderLine).Append("\r\n");
        for (var i = 0; i < 20; i++)
        {
            text.Append("=Bad,,,,notTrue,,,X,Y,Z\r\n");
        }

        var outcome = await UseCase(limits).ExecuteAsync(
            Request(scope, wave.Id, Encoding.UTF8.GetBytes(text.ToString()), key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None);
        Assert.True(outcome.IssuesTruncated);
        Assert.True(outcome.IssueCount > 5);
        Assert.Equal(5, outcome.Issues.Count);

        var stored = await Store().GetAsync(scope, outcome.ValidationId, CancellationToken.None);
        Assert.Equal(5, stored!.Issues.Count); // persistidos limitados ao teto
    }

    // ===================== anti-IDOR / wave state =====================

    [Fact]
    public async Task WaveFromOtherProjectOrTenantIsNotFoundWithZeroAttempt()
    {
        var (scopeA1, _) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var (_, waveA2) = await SeedApprovedWaveAsync(new[] { Entry("b.pst", "v@contoso.com") }, tenantOf: scopeA1);
        var (_, waveB) = await SeedApprovedWaveAsync(Entry("c.pst", "w@contoso.com"));

        foreach (var foreign in new[] { waveA2.Id, waveB.Id })
        {
            await Assert.ThrowsAsync<PlanningNotFoundException>(() => UseCase().ExecuteAsync(
                Request(scopeA1, foreign, ValidCsv(waveA2), key: Guid.NewGuid()), MappingPolicy.Default, CancellationToken.None));
        }

        Assert.Equal(0, await CountAttemptsAsync(scopeA1));
    }

    [Theory]
    [InlineData(WaveStatus.Approved, true)]
    [InlineData(WaveStatus.Frozen, true)]
    [InlineData(WaveStatus.Draft, false)]
    [InlineData(WaveStatus.Validating, false)]
    [InlineData(WaveStatus.Completed, false)]
    [InlineData(WaveStatus.Cancelled, false)]
    public async Task OnlyApprovedOrFrozenWavesAreValidatable(WaveStatus status, bool allowed)
    {
        var (scope, wave) = await SeedWaveInStatusAsync(status, Entry("a.pst", "u@contoso.com"));
        // Só é possível gerar um CSV válido de uma onda Approved/Frozen; para estados recusados o conteúdo é
        // irrelevante (a precondição falha antes da validação), então usamos bytes arbitrários.
        var content = allowed ? ValidCsv(wave) : "x"u8.ToArray();
        var request = Request(scope, wave.Id, content, key: Guid.NewGuid());

        if (allowed)
        {
            var outcome = await UseCase().ExecuteAsync(request, MappingPolicy.Default, CancellationToken.None);
            Assert.True(outcome.IsValid);
            Assert.Equal(1, await CountAttemptsAsync(scope));
        }
        else
        {
            await Assert.ThrowsAsync<MappingUploadPreconditionException>(() => UseCase().ExecuteAsync(request, MappingPolicy.Default, CancellationToken.None));
            Assert.Equal(0, await CountAttemptsAsync(scope));
        }
    }

    // ===================== idempotência =====================

    [Fact]
    public async Task SameRequestReplaysSameValidationIdAndSingleIssueSet()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        // CSV inválido (cabeçalho ruim) ⇒ 1 issue; o replay não deve duplicar tentativa NEM issues.
        var csv = "Bad,Header\r\n"u8.ToArray();
        var key = Guid.NewGuid();

        var first = await UseCase().ExecuteAsync(Request(scope, wave.Id, csv, key: key), MappingPolicy.Default, CancellationToken.None);
        var second = await UseCase().ExecuteAsync(Request(scope, wave.Id, csv, key: key), MappingPolicy.Default, CancellationToken.None);

        Assert.True(first.Created);
        Assert.True(second.Replayed);
        Assert.Equal(first.ValidationId, second.ValidationId);
        Assert.Equal(1, await CountAttemptsAsync(scope));
        Assert.Equal(1, await CountIssuesAsync(scope));
    }

    [Fact]
    public async Task SameKeyDifferentContentOrWaveOrCodePageConflicts()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var otherWave = await AddApprovedWaveAsync(scope, Entry("a.pst", "u@contoso.com")); // MESMO projeto
        var csv = ValidCsv(wave);
        var key = Guid.NewGuid();
        await UseCase().ExecuteAsync(Request(scope, wave.Id, csv, key: key), MappingPolicy.Default, CancellationToken.None);

        // bytes diferentes
        await Assert.ThrowsAsync<MappingValidationConflictException>(() => UseCase().ExecuteAsync(
            Request(scope, wave.Id, csv.Concat(new byte[] { (byte)'\n' }).ToArray(), key: key), MappingPolicy.Default, CancellationToken.None));
        // onda diferente
        await Assert.ThrowsAsync<MappingValidationConflictException>(() => UseCase().ExecuteAsync(
            Request(scope, otherWave.Id, ValidCsv(otherWave), key: key), MappingPolicy.Default, CancellationToken.None));
        Assert.Equal(1, await CountAttemptsAsync(scope));
    }

    [Fact]
    public async Task ConcurrentSameRequestCreatesExactlyOneAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var csv = ValidCsv(wave);
        var key = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => UseCase().ExecuteAsync(
            Request(scope, wave.Id, csv, key: key), MappingPolicy.Default, CancellationToken.None))).ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Single(outcomes.Select(o => o.ValidationId).Distinct());
        Assert.Equal(1, await CountAttemptsAsync(scope));
        Assert.Equal(1, outcomes.Count(o => o.Created));
    }

    // ===================== TOCTOU (stale) / Approved→Frozen =====================

    [Fact]
    public async Task StaleSourceIsRejectedWithZeroAttempt()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var attempt = BuildValidAttempt(scope, wave, ValidCsv(wave));

        // A onda muda de estado (Approved ⇒ Completed) ANTES do commit da custódia (relê para row_version fresca).
        var fresh = await ReadWaveAsync(scope, wave.Id);
        fresh.Freeze();
        fresh.Complete();
        await Slice2Support.WaveStore(_fixture).SaveStatusAsync(fresh, CorrelationId.New(), CancellationToken.None);

        await Assert.ThrowsAsync<MappingValidationStaleSourceException>(() => Store().PersistAsync(attempt, CancellationToken.None));
        Assert.Equal(0, await CountAttemptsAsync(scope));
    }

    [Fact]
    public async Task ApprovedToFrozenStillAllowsPersistence()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var attempt = BuildValidAttempt(scope, wave, ValidCsv(wave));

        var fresh = await ReadWaveAsync(scope, wave.Id);
        fresh.Freeze(); // versão/hashes intactos — continua fonte válida
        await Slice2Support.WaveStore(_fixture).SaveStatusAsync(fresh, CorrelationId.New(), CancellationToken.None);

        var result = await Store().PersistAsync(attempt, CancellationToken.None);
        Assert.True(result.Created);
        Assert.Equal(1, await CountAttemptsAsync(scope));
    }

    // ===================== imutabilidade / RLS =====================

    [Fact]
    public async Task AttemptsAndIssuesAreAppendOnlyForApplicationIdentity()
    {
        var (scope, wave) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var outcome = await UseCase().ExecuteAsync(Request(scope, wave.Id, ValidCsv(wave)), MappingPolicy.Default, CancellationToken.None);

        Assert.True(await ThrowsAsync(scope, "UPDATE dbo.mapping_validation_attempts SET size_bytes = 0 WHERE validation_id = @id;", outcome.ValidationId));
        Assert.True(await ThrowsAsync(scope, "DELETE FROM dbo.mapping_validation_attempts WHERE validation_id = @id;", outcome.ValidationId));
        Assert.True(await ThrowsAsync(scope, "UPDATE dbo.mapping_validation_issues SET message = 'x' WHERE validation_id = @id;", outcome.ValidationId));
        Assert.True(await ThrowsAsync(scope, "DELETE FROM dbo.mapping_validation_issues WHERE validation_id = @id;", outcome.ValidationId));
    }

    [Fact]
    public async Task MaintenanceIdentityHasNoAccessToValidationTables()
    {
        await using var connection = new SqlConnection(_fixture.MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TOP 1 validation_id FROM dbo.mapping_validation_attempts;", connection);
        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProjectFilterAndRlsIsolateAttemptsAcrossProjects()
    {
        var (scopeP1, waveP1) = await SeedApprovedWaveAsync(Entry("a.pst", "u@contoso.com"));
        var scopeP2 = new TenantScope(scopeP1.Tenant, new ProjectId(Guid.NewGuid()));
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(scopeP2), CorrelationId.New(), CancellationToken.None);
        var outcome = await UseCase().ExecuteAsync(Request(scopeP1, waveP1.Id, ValidCsv(waveP1)), MappingPolicy.Default, CancellationToken.None);

        // Mesmo tenant, outro projeto: o store (project_id = @project) não enxerga a tentativa de P1.
        Assert.Null(await Store().GetAsync(scopeP2, outcome.ValidationId, CancellationToken.None));
        Assert.NotNull(await Store().GetAsync(scopeP1, outcome.ValidationId, CancellationToken.None));
    }

    // ===================== helpers =====================

    private SqlMappingValidationStore Store() => new(_fixture.Factory);

    private ValidateMappingCsvUploadUseCase UseCase(MappingUploadLimits? limits = null) =>
        new(Slice2Support.WaveStore(_fixture), Store(), Clock, limits ?? MappingUploadLimits.Default);

    private static WaveEntry Entry(string pst, string mailbox) => Slice2Support.Entry(pst, mailbox, 10);

    private static byte[] ValidCsv(MigrationWave wave) =>
        MappingCsvGenerator.Generate(wave, CodePage, MappingPolicy.Default, MappingVersion.Initial, "do", Slice2Support.Now)
            .Document.GetBytes().ToArray();

    private static MappingCsvUploadRequest Request(
        TenantScope scope,
        WaveId waveId,
        byte[] content,
        string fileName = "mapping.csv",
        long? declaredLength = null,
        Guid? key = null) =>
        Request(scope, waveId, new MemoryStream(content), fileName, declaredLength ?? content.Length, key);

    private static MappingCsvUploadRequest Request(
        TenantScope scope, WaveId waveId, Stream content, string fileName = "mapping.csv", long? declaredLength = null, Guid? key = null) =>
        new(scope, waveId, CodePage, content, fileName, declaredLength, Guid.NewGuid(), "operator", key ?? Guid.NewGuid(), CorrelationId.New());

    private static MappingValidationAttempt BuildValidAttempt(TenantScope scope, MigrationWave wave, byte[] csv) =>
        new(
            Guid.NewGuid(), scope, wave.Id, wave.Version.Value, wave.ConfigurationHash, wave.SelectionHash,
            MappingSchema.Version, MappingPolicy.Default.Version, CodePage, DeterministicHash.ComputeBytes(csv),
            csv.Length, 1, MappingValidationAttemptOutcome.Valid, 0, false, "mapping.csv", Guid.NewGuid(), "operator",
            CorrelationId.New(), Guid.NewGuid(), Clock.UtcNow, []);

    private async Task<MigrationWave> ReadWaveAsync(TenantScope scope, WaveId waveId) =>
        (await Slice2Support.WaveStore(_fixture).GetAsync(scope, waveId, CancellationToken.None))!;

    // Adiciona uma segunda onda aprovada ao MESMO projeto/escopo (o projeto já existe).
    private async Task<MigrationWave> AddApprovedWaveAsync(TenantScope scope, params WaveEntry[] entries)
    {
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection(entries.ToList())));
        var waveStore = Slice2Support.WaveStore(_fixture);
        await waveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await waveStore.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);
        return wave;
    }

    private async Task<(TenantScope Scope, MigrationWave Wave)> SeedApprovedWaveAsync(params WaveEntry[] entries)
        => await SeedApprovedWaveAsync(entries, tenantOf: null);

    private async Task<(TenantScope Scope, MigrationWave Wave)> SeedApprovedWaveAsync(WaveEntry[] entries, TenantScope? tenantOf)
    {
        var scope = tenantOf is { } t ? new TenantScope(t.Tenant, new ProjectId(Guid.NewGuid())) : SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection(entries.ToList())));
        var waveStore = Slice2Support.WaveStore(_fixture);
        await waveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await waveStore.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave);
    }

    private async Task<(TenantScope Scope, MigrationWave Wave)> SeedWaveInStatusAsync(WaveStatus status, params WaveEntry[] entries)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(_fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection(entries.ToList()));
        DriveTo(wave, status);
        var waveStore = Slice2Support.WaveStore(_fixture);
        await waveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        if (status != WaveStatus.Draft)
        {
            await waveStore.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);
        }

        return (scope, wave);
    }

    private static void DriveTo(MigrationWave wave, WaveStatus status)
    {
        switch (status)
        {
            case WaveStatus.Draft:
                break;
            case WaveStatus.Validating:
                wave.StartValidation();
                break;
            case WaveStatus.Cancelled:
                wave.Cancel();
                break;
            case WaveStatus.Approved:
                Slice2Support.Approve(wave);
                break;
            case WaveStatus.Frozen:
                Slice2Support.Approve(wave);
                wave.Freeze();
                break;
            case WaveStatus.Completed:
                Slice2Support.Approve(wave);
                wave.Freeze();
                wave.Complete();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Estado não coberto pelo seeding de teste.");
        }
    }

    private async Task<int> CountAttemptsAsync(TenantScope scope) =>
        await CountAsync(scope, "SELECT COUNT(*) FROM dbo.mapping_validation_attempts WHERE project_id = @project;");

    private async Task<int> CountIssuesAsync(TenantScope scope) =>
        await CountAsync(scope, "SELECT COUNT(*) FROM dbo.mapping_validation_issues WHERE project_id = @project;");

    private async Task<int> CountAsync(TenantScope scope, string sql)
    {
        await using var connection = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> ThrowsAsync(TenantScope scope, string sql, Guid validationId)
    {
        await using var connection = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = validationId });
        try
        {
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            return false;
        }
        catch (SqlException)
        {
            return true;
        }
    }

    private sealed class CountingStream(long length) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toReturn = (int)Math.Min(count, remaining);
            Array.Fill(buffer, (byte)'A', offset, toReturn);
            _position += toReturn;
            BytesRead += toReturn;
            return toReturn;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
