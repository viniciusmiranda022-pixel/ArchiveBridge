using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
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

    public static SqlPlanningStore PlanningStore(SqlServerFixture fixture) => new(fixture.Factory);

    public static SqlMappingStore MappingStore(SqlServerFixture fixture) => new(fixture.Factory);

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

    public static WaveEntry Entry(string pst, string mailbox, long size) =>
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
