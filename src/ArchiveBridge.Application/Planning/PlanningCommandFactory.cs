using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Planning;

/// <summary>
/// Constrói comandos duráveis já VINCULADOS à versão/estado que os originou — o contexto é fotografado
/// do agregado no momento do enfileiramento. Assim, se o estado avançar antes da execução, o worker
/// detecta a divergência e recusa o comando obsoleto (<see cref="StaleCommandContextException"/>) em
/// vez de atuar sobre uma versão posterior.
/// </summary>
public static class PlanningCommandFactory
{
    /// <summary>Comando ValidateProject vinculado à versão/hash de configuração corrente do projeto.</summary>
    public static PlanningCommand ValidateProject(MigrationProject project, CorrelationId correlation)
    {
        ArgumentNullException.ThrowIfNull(project);
        var scope = new TenantScope(project.Tenant, project.Id);
        var context = new PlanningCommandContext(
            PlanningCommandContext.CurrentSchemaVersion,
            project.ConfigurationVersion.Value,
            project.ConfigurationHash,
            ExpectedWaveVersion: null,
            ExpectedSelectionHash: null,
            ExpectedTargetRootFolder: null,
            MappingSchemaVersion: null,
            MappingGeneratorVersion: null,
            MappingPolicyVersion: null);
        return Validated(new PlanningCommand(PlanningCommandKind.ValidateProject, scope, Wave: null, ContentCodePage: null, GeneratedBy: null, correlation, context));
    }

    /// <summary>Comando ValidateWave vinculado à versão/hash de seleção e destino correntes da onda.</summary>
    public static PlanningCommand ValidateWave(MigrationWave wave, CorrelationId correlation)
    {
        ArgumentNullException.ThrowIfNull(wave);
        return Validated(new PlanningCommand(
            PlanningCommandKind.ValidateWave, ScopeOf(wave), wave.Id, ContentCodePage: null, GeneratedBy: null, correlation, WaveContext(wave)));
    }

    /// <summary>Comando FreezeWave vinculado à versão/hash de seleção e destino correntes da onda.</summary>
    public static PlanningCommand FreezeWave(MigrationWave wave, CorrelationId correlation)
    {
        ArgumentNullException.ThrowIfNull(wave);
        return Validated(new PlanningCommand(
            PlanningCommandKind.FreezeWave, ScopeOf(wave), wave.Id, ContentCodePage: null, GeneratedBy: null, correlation, WaveContext(wave)));
    }

    /// <summary>Comando GenerateMappingCsv vinculado à onda e às versões de esquema/gerador/política do mapping.</summary>
    public static PlanningCommand GenerateMappingCsv(
        MigrationWave wave, ContentCodePage contentCodePage, string generatedBy, MappingPolicy policy, CorrelationId correlation)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(policy);
        var context = WaveContext(wave) with
        {
            MappingSchemaVersion = MappingSchema.Version,
            MappingGeneratorVersion = MappingCsvGenerator.GeneratorVersion,
            MappingPolicyVersion = policy.Version,
        };
        return Validated(new PlanningCommand(
            PlanningCommandKind.GenerateMappingCsv, ScopeOf(wave), wave.Id, contentCodePage.Value, generatedBy, correlation, context));
    }

    private static PlanningCommand Validated(PlanningCommand command)
    {
        PlanningCommandValidation.EnsureValid(command);
        return command;
    }

    private static TenantScope ScopeOf(MigrationWave wave) => new(wave.Tenant, wave.Project);

    private static PlanningCommandContext WaveContext(MigrationWave wave) =>
        new(
            PlanningCommandContext.CurrentSchemaVersion,
            ExpectedProjectConfigurationVersion: null,
            wave.ConfigurationHash,
            wave.Version.Value,
            wave.SelectionHash,
            wave.TargetRootFolder.Value,
            MappingSchemaVersion: null,
            MappingGeneratorVersion: null,
            MappingPolicyVersion: null);
}
