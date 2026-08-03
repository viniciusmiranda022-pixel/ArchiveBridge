using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>B2: contexto OBRIGATÓRIO por tipo — validação fail-closed no código (EnsureValid).</summary>
public sealed class RequiredContextValidationTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));

    private static PlanningCommand ValidateProject() =>
        PlanningCommandFactory.ValidateProject(Slice2Support.NewProject(Scope), CorrelationId.New());

    private static PlanningCommand ValidateWave() =>
        PlanningCommandFactory.ValidateWave(Wave(), CorrelationId.New());

    private static PlanningCommand GenerateMapping() =>
        PlanningCommandFactory.GenerateMappingCsv(Wave(), new ContentCodePage(1252), "do", MappingPolicy.Default, CorrelationId.New());

    private static MigrationWave Wave() =>
        Slice2Support.NewWave(Scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));

    [Fact]
    public void ValidCommandsPass()
    {
        PlanningCommandValidation.EnsureValid(ValidateProject());
        PlanningCommandValidation.EnsureValid(ValidateWave());
        PlanningCommandValidation.EnsureValid(GenerateMapping());
    }

    [Fact]
    public void ValidateProjectMissingConfigVersionOrHashIsRejected()
    {
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(
            ValidateProject() with { Context = ValidateProject().Context with { ExpectedProjectConfigurationVersion = null } }));
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(
            ValidateProject() with { Context = ValidateProject().Context with { ExpectedConfigurationHash = null } }));
    }

    [Fact]
    public void ValidateWaveMissingAnyWaveFieldIsRejected()
    {
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(ValidateWave() with { Wave = null }));
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(
            ValidateWave() with { Context = ValidateWave().Context with { ExpectedWaveVersion = null } }));
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(
            ValidateWave() with { Context = ValidateWave().Context with { ExpectedSelectionHash = null } }));
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(
            ValidateWave() with { Context = ValidateWave().Context with { ExpectedTargetRootFolder = null } }));
    }

    [Fact]
    public void GenerateMappingMissingMappingFieldsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(GenerateMapping() with { ContentCodePage = null }));
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(GenerateMapping() with { GeneratedBy = null }));
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(
            GenerateMapping() with { Context = GenerateMapping().Context with { MappingPolicyVersion = null } }));
        Assert.Throws<ArgumentException>(() => PlanningCommandValidation.EnsureValid(
            GenerateMapping() with { Context = GenerateMapping().Context with { MappingGeneratorVersion = null } }));
    }
}

/// <summary>B2: a mesma exigência de contexto por tipo é reforçada no BANCO (CK_pc_required_by_type).</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2RequiredContextSqlTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task MalformedPlanningCommandRowIsRejectedByCheck()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Slice2Support.Now);
        var jobId = await fixture.Store(clock).CreateAsync(
            new CreateJobCommand(scope, Workload.Control, new JobPriority(0), CorrelationId.New()), CancellationToken.None);

        // command_type = 0 (ValidateProject) SEM expected_project_configuration_version ⇒ viola o CHECK.
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.planning_commands
                (job_id, tenant_id, project_id, command_type, correlation_id, created_at_utc, command_schema_version)
            VALUES (@job, @tenant, @project, 0, @corr, SYSUTCDATETIME(), 1);
            """,
            connection.Connection);
        command.Parameters.AddWithValue("@job", jobId.Value);
        command.Parameters.AddWithValue("@tenant", scope.Tenant.Value);
        command.Parameters.AddWithValue("@project", scope.Project.Value);
        command.Parameters.AddWithValue("@corr", Guid.NewGuid());

        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(CancellationToken.None));
    }
}
