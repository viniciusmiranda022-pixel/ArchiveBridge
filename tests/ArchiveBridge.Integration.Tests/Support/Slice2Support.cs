using ArchiveBridge.Application.Mapping;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Application.Waves;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.Planning;
using ArchiveBridge.Infrastructure.Projects;
using ArchiveBridge.Infrastructure.Waves;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>Fábricas e builders compartilhados pelos testes de integração da Vertical Slice 2.</summary>
internal static class Slice2Support
{
    public static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public static SqlProjectStore ProjectStore(SqlServerFixture fixture) => new(fixture.Factory);

    public static SqlWaveStore WaveStore(SqlServerFixture fixture) => new(fixture.Factory);

    public static SqlMappingStore MappingStore(SqlServerFixture fixture) => new(fixture.Factory);

    public static SqlCapacityAssessmentDecisionStore DecisionStore(SqlServerFixture fixture) => new(fixture.Factory);

    public static FileSystemMappingArtifactStore ArtifactStore(SqlServerFixture fixture) => new(fixture.ArtifactRoot);

    /// <summary>Persiste uma geração de mapping publicando o artefato imutável (como o caso de uso faz).</summary>
    public static Task<MappingCsvVersion> SaveMapping(
        SqlServerFixture fixture, TenantScope scope, MappingGenerationResult result, CancellationToken cancellationToken) =>
        MappingStore(fixture).SaveAsync(
            scope,
            result,
            fence: null,
            (version, token) => ArtifactStore(fixture).SaveAsync(
                new MappingArtifactDescriptor(scope, result.Version.Wave, version),
                result.Document.GetBytes(),
                result.Document.ContentSha256,
                token),
            cancellationToken);

    public static SqlPlanningCommandInbox Inbox(SqlServerFixture fixture, IClock clock) => new(fixture.Factory, clock);

    public static ValidateWaveUseCase ValidateWave(SqlServerFixture fixture, IClock clock) =>
        new(WaveStore(fixture), DecisionStore(fixture), clock);

    public static GenerateMappingCsvUseCase GenerateMapping(SqlServerFixture fixture, IClock clock) =>
        new(WaveStore(fixture), MappingStore(fixture), ArtifactStore(fixture), clock);

    public static RecordMicrosoftAssessmentDecisionUseCase RecordDecision(SqlServerFixture fixture, IClock clock) =>
        new(WaveStore(fixture), DecisionStore(fixture), ValidateWave(fixture, clock), clock);

    public static PlanningCommandProcessor Processor(SqlServerFixture fixture, IClock clock) =>
        new(
            Inbox(fixture, clock),
            fixture.Store(clock),
            ProjectStore(fixture),
            WaveStore(fixture),
            new ValidateProjectUseCase(ProjectStore(fixture), clock),
            ValidateWave(fixture, clock),
            GenerateMapping(fixture, clock),
            new FreezeWaveUseCase(WaveStore(fixture)),
            MappingPolicy.Default,
            clock);

    public static Sha256Hash ConfigHash() =>
        new ProjectConfiguration(new TargetTenant("contoso.onmicrosoft.com"), TargetArchivePolicy.OnlineArchive)
            .ComputeHash();

    public static MigrationProject NewProject(TenantScope scope) =>
        MigrationProject.Create(
            scope.Project,
            scope.Tenant,
            new ProjectName("Projeto"),
            new ProjectOwner("owner@contoso.com"),
            new ProjectConfiguration(new TargetTenant("contoso.onmicrosoft.com"), TargetArchivePolicy.OnlineArchive),
            Now);

    /// <summary>Entrada com identidade de archive RESOLVIDA (manifesto autorizado) — fluxo de produção.</summary>
    public static WaveEntry Entry(string pst, string mailbox, long size) =>
        new($"/src/{pst}", pst, new ArchiveRef(mailbox, new TargetArchiveId(mailbox)), size, 1);

    /// <summary>Entrada com identidade de archive resolvida explicitamente (para testes de alias).</summary>
    public static WaveEntry EntryWithArchive(string pst, string mailbox, TargetArchiveId archive, long size) =>
        new($"/src/{pst}", pst, new ArchiveRef(mailbox, archive), size, 1);

    /// <summary>Entrada com identidade de archive NÃO resolvida (derivada só da mailbox).</summary>
    public static WaveEntry UnresolvedEntry(string pst, string mailbox, long size) =>
        new($"/src/{pst}", pst, new ArchiveRef(mailbox), size, 1);

    public static TargetRootFolder UniqueFolder() =>
        TargetRootFolder.ForWave(Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N")[..8]);

    public static MigrationWave NewWave(TenantScope scope, WaveSelection selection, TargetRootFolder? folder = null) =>
        MigrationWave.Create(
            WaveId.New(),
            scope.Tenant,
            scope.Project,
            new WaveName("Onda"),
            folder ?? UniqueFolder(),
            ConfigHash(),
            selection,
            Now);

    /// <summary>Conduz a onda até Approved no domínio (validações concluídas) para os testes.</summary>
    public static MigrationWave Approve(MigrationWave wave)
    {
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("decision.owner", Now);
        return wave;
    }
}
