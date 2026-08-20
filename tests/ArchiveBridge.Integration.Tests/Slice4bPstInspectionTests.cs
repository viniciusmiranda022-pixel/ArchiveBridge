using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4B, Passo 1 (SQL Server real) — PST Inspection &amp; Inventory: custódia server-side, inspeção
/// read-only, idempotência/concorrência, staleness fail-closed, diagnóstico estruturado nunca-crash,
/// anti-IDOR e preservação byte-for-byte.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4bPstInspectionTests(SqlServerFixture fixture)
{
    private async Task<TenantScope> NewProjectScopeAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture)
            .AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        return scope;
    }

    private async Task<(TenantScope Scope, ArtifactId Artifact, byte[] Bytes)> RegisterAsync(byte[] bytes, string name)
    {
        var scope = await NewProjectScopeAsync();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, name, bytes);
        var hash = DeterministicHash.ComputeBytes(bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture)
            .RegisterAsync(scope.Tenant, scope.Project, new PstRelativePath(relative), hash, bytes.Length, CancellationToken.None);
        return (scope, artifact.Id, bytes);
    }

    [Fact]
    public async Task ValidUnicodePstIsDiagnosedValidAndBecomesCanonical()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var (scope, artifact, _) = await RegisterAsync(bytes, "valid.pst");
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.Completed, result.Outcome);
        Assert.Equal(PstStructuralDiagnostic.Valid, result.Diagnostic);
        Assert.Equal(PstFormatVariant.Unicode2013Plus, result.FormatVariant);
        Assert.True(result.IsCanonical);
        Assert.Equal(bytes.Length, result.ObservedSizeBytes);

        // Preservação byte-for-byte: os bytes no disco são EXATAMENTE os mesmos após a inspeção.
        var onDisk = File.ReadAllBytes(Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), "valid.pst"));
        Assert.Equal(bytes, onDisk);
    }

    [Fact]
    public async Task TruncatedFileIsDiagnosedTooSmallNeverThrows()
    {
        var bytes = new byte[] { 0x21, 0x42, 0x44 }; // menor que o cabeçalho mínimo (12 bytes)
        var (scope, artifact, _) = await RegisterAsync(bytes, "truncated.pst");
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.Completed, result.Outcome);
        Assert.Equal(PstStructuralDiagnostic.TooSmall, result.Diagnostic);
        Assert.True(result.IsCanonical); // diagnóstico definitivo, hash bate com o registrado — nunca "sucesso falso" de PST válido, mas a TENTATIVA em si é canônica.
    }

    [Fact]
    public async Task InvalidSignatureFileIsDiagnosedStructurallyNeverThrows()
    {
        var bytes = Slice4bPstProcessingSupport.InvalidSignatureBytes();
        var (scope, artifact, _) = await RegisterAsync(bytes, "not-a-pst.bin");
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.Completed, result.Outcome);
        Assert.Equal(PstStructuralDiagnostic.InvalidSignature, result.Diagnostic);
    }

    [Fact]
    public async Task UnsupportedVersionIsDiagnosedStructurally()
    {
        var bytes = Slice4bPstProcessingSupport.UnsupportedVersionHeader();
        var (scope, artifact, _) = await RegisterAsync(bytes, "future-version.pst");
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstStructuralDiagnostic.UnsupportedVersion, result.Diagnostic);
        Assert.Equal(PstFormatVariant.Unknown, result.FormatVariant);
    }

    [Fact]
    public async Task HashDivergenceSinceRegistrationFailsClosedAsStale()
    {
        var scope = await NewProjectScopeAsync();
        var original = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, "drifting.pst", original);
        var registeredHash = DeterministicHash.ComputeBytes(original);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture)
            .RegisterAsync(scope.Tenant, scope.Project, new PstRelativePath(relative), registeredHash, original.Length, CancellationToken.None);

        // O arquivo muda DEPOIS do registro de custódia (fonte alterada) — a inspeção deve falhar fechado.
        var mutated = Slice4bPstProcessingSupport.ValidUnicodeHeader(totalSize: 5000);
        File.WriteAllBytes(Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), "drifting.pst"), mutated);

        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);
        var result = await useCase.ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.Stale, result.Outcome);
        Assert.False(result.IsCanonical);
        Assert.Equal(registeredHash, result.ExpectedHash);
        Assert.NotEqual(registeredHash, result.ObservedHash);

        // Uma segunda divergência não colide no índice único filtrado (que só protege Completed/canônicos):
        // cada tentativa Stale é sua própria linha de evidência, sem reaproveitar a anterior como sucesso.
        var second = await useCase.ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PstInspectionOutcome.Stale, second.Outcome);
        Assert.NotEqual(result.Id, second.Id);
    }

    [Fact]
    public async Task OversizedFileFailsClosedAsLimitExceeded()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader(totalSize: 4096);
        var (scope, artifact, _) = await RegisterAsync(bytes, "oversized.pst");
        var tinyLimit = new PstStorageOptions { RootPath = Slice4bPstProcessingSupport.PstRoot(fixture), MaxSizeBytes = 100 };
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture, options: tinyLimit);

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.LimitExceeded, result.Outcome);
        Assert.False(result.IsCanonical);
        Assert.Null(result.ObservedHash);

        // O arquivo original nunca foi tocado, mesmo tendo sido rejeitado por tamanho.
        var onDisk = File.ReadAllBytes(Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), "oversized.pst"));
        Assert.Equal(bytes, onDisk);
    }

    [Fact]
    public async Task CrossTenantAndCrossProjectAreDeniedIndistinguishablyFromNotFound()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var (ownerScope, artifact, _) = await RegisterAsync(bytes, "owned.pst");
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

        var crossProject = new TenantScope(ownerScope.Tenant, SqlServerFixture.NewScope().Project);
        var crossTenant = new TenantScope(SqlServerFixture.NewScope().Tenant, ownerScope.Project);

        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            useCase.ExecuteAsync(crossProject, artifact, CorrelationId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            useCase.ExecuteAsync(crossTenant, artifact, CorrelationId.New(), CancellationToken.None));

        // O dono continua conseguindo inspecionar normalmente (a negação foi só do escopo errado).
        var ownerResult = await useCase.ExecuteAsync(ownerScope, artifact, CorrelationId.New(), CancellationToken.None);
        Assert.True(ownerResult.IsCanonical);
    }

    [Fact]
    public async Task IdempotentReplayReturnsTheSameCanonicalRecordWithoutANewRow()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var (scope, artifact, _) = await RegisterAsync(bytes, "replay.pst");
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

        var first = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.Id, second.Id); // mesma linha canônica reaproveitada — nenhum novo checkpoint criado.
        Assert.Equal(first.CompletedAtUtc, second.CompletedAtUtc);
    }

    [Fact]
    public async Task ConcurrentInspectionOfTheSameArtifactConvergesToExactlyOneCanonicalRecord()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var (scope, artifact, _) = await RegisterAsync(bytes, "concurrent.pst");

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => Slice4bPstProcessingSupport.UseCase(fixture)
                .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.IsCanonical));
        Assert.Single(results.Select(r => r.Id).Distinct()); // uma corrida de 6 concorrentes ⇒ UMA linha canônica.

        var finalCheck = await Slice4bPstProcessingSupport.InspectionStore(fixture)
            .FindCanonicalAsync(scope, artifact, DeterministicHash.ComputeBytes(bytes), CancellationToken.None);
        Assert.NotNull(finalCheck);
        Assert.Equal(results[0].Id, finalCheck!.Id);
    }

    // ---- AB-4B-002 item 1: ReadError nunca ocupa nem é devolvido como canônico ----

    [Fact]
    public async Task ReadErrorIsNeverCanonicalAndDoesNotBlockASubsequentSuccessfulInspection()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var hash = DeterministicHash.ComputeBytes(bytes);
        var scope = await NewProjectScopeAsync();
        const string relative = "delayed.pst"; // registrado em custódia, mas o arquivo ainda NÃO existe no disco.
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture)
            .RegisterAsync(scope.Tenant, scope.Project, new PstRelativePath(relative), hash, bytes.Length, CancellationToken.None);
        var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

        // 1ª tentativa: arquivo inexistente ⇒ ReadError, persistido como evidência, NUNCA canônico.
        var first = await useCase.ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PstInspectionOutcome.Completed, first.Outcome);
        Assert.Equal(PstStructuralDiagnostic.ReadError, first.Diagnostic);
        Assert.False(first.IsCanonical);

        // Réplay imediato: o ReadError anterior NÃO é reaproveitado como se fosse canônico — a engine é
        // reinvocada de verdade (arquivo continua ausente) e produz sua PRÓPRIA linha de evidência.
        var stillMissing = await useCase.ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PstStructuralDiagnostic.ReadError, stillMissing.Diagnostic);
        Assert.NotEqual(first.Id, stillMissing.Id);

        // A leitura é restabelecida: o arquivo passa a existir com o MESMO hash registrado em custódia.
        Slice4bPstProcessingSupport.WriteFile(fixture, relative, bytes);

        // 2ª tentativa real: a inspeção é reexecutada de verdade (nunca bloqueada pelo ReadError anterior) e
        // agora se torna a canônica.
        var recovered = await useCase.ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PstStructuralDiagnostic.Valid, recovered.Diagnostic);
        Assert.True(recovered.IsCanonical);

        // Réplay subsequente: agora sim reaproveita o canônico real, sem reinvocar a engine (mesma linha).
        var replay = await useCase.ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(recovered.Id, replay.Id);

        var canonicalRow = await Slice4bPstProcessingSupport.InspectionStore(fixture)
            .FindCanonicalAsync(scope, artifact.Id, hash, CancellationToken.None);
        Assert.NotNull(canonicalRow);
        Assert.Equal(recovered.Id, canonicalRow!.Id);
    }

    [Fact]
    public async Task ConcurrentReadErrorAttemptsAreNotConfusedWithACanonicalRace()
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var hash = DeterministicHash.ComputeBytes(bytes);
        var scope = await NewProjectScopeAsync();
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture)
            .RegisterAsync(scope.Tenant, scope.Project, new PstRelativePath("missing-concurrent.pst"), hash, bytes.Length, CancellationToken.None);
        // O arquivo NUNCA é escrito nesta prova — toda tentativa concorrente resulta em ReadError.

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Slice4bPstProcessingSupport.UseCase(fixture)
                .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(PstStructuralDiagnostic.ReadError, r.Diagnostic));
        Assert.All(results, r => Assert.False(r.IsCanonical));
        // Nenhuma delas disputa o índice único filtrado (que só protege is_canonical=1) — cada tentativa é
        // sua própria linha de evidência, sem PstInspectionConflictException nem colisão de "canônico".
        Assert.Equal(4, results.Select(r => r.Id).Distinct().Count());
    }

    // ---- AB-4B-002 item 3: symlink/reparse point na cadeia de custódia falha fechado como ReadError ----

    [Fact]
    public async Task SymlinkedArtifactPathIsRejectedFailClosedAsReadError()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ab_pst_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "real.pst");
        File.WriteAllBytes(outsideFile, Slice4bPstProcessingSupport.ValidUnicodeHeader());

        var root = Slice4bPstProcessingSupport.PstRoot(fixture);
        Directory.CreateDirectory(root);
        var linkPath = Path.Combine(root, "escape-link.pst");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Ambiente sem suporte a symlink: cenário não aplicável (mesma convenção de ArtifactPathContainmentTests).
        }

        try
        {
            var scope = await NewProjectScopeAsync();
            var registeredHash = DeterministicHash.ComputeBytes(Slice4bPstProcessingSupport.ValidUnicodeHeader());
            var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture)
                .RegisterAsync(scope.Tenant, scope.Project, new PstRelativePath("escape-link.pst"), registeredHash, 4096, CancellationToken.None);
            var useCase = Slice4bPstProcessingSupport.UseCase(fixture);

            var result = await useCase.ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

            // Falha fechado: nunca lê através do symlink, nunca reporta sucesso — mesmo que o alvo real
            // seja um PST válido com o hash "certo", o caminho de custódia nunca deveria ser um reparse point.
            Assert.Equal(PstInspectionOutcome.Completed, result.Outcome);
            Assert.Equal(PstStructuralDiagnostic.ReadError, result.Diagnostic);
            Assert.False(result.IsCanonical);
        }
        finally
        {
            File.Delete(linkPath);
            Directory.Delete(outside, recursive: true);
        }
    }
}
