using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Planning;

/// <summary>
/// Confere o contexto versionado de um comando durável contra o estado CORRENTE do agregado, antes de
/// executar. Um schema desconhecido, ou qualquer campo esperado que não coincida (versão/hash de
/// configuração, versão/hash de seleção, pasta de destino, versões de esquema/gerador/política do
/// mapping), indica comando OBSOLETO ⇒ <see cref="StaleCommandContextException"/> (fail-closed). Um
/// retry da MESMA versão passa (os campos ainda coincidem).
/// </summary>
internal static class PlanningCommandContextGuard
{
    public static void EnsureSchema(PlanningCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SchemaVersion != PlanningCommandContext.CurrentSchemaVersion)
        {
            throw new StaleCommandContextException(
                $"schema de comando desconhecido ({context.SchemaVersion}); esperado {PlanningCommandContext.CurrentSchemaVersion}.");
        }
    }

    public static void EnsureProject(PlanningCommandContext context, MigrationProject project)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(project);
        EnsureSchema(context);
        EnsureVersion(context.ExpectedProjectConfigurationVersion, project.ConfigurationVersion.Value, "versão de configuração do projeto");
        EnsureHash(context.ExpectedConfigurationHash, project.ConfigurationHash, "hash de configuração do projeto");
    }

    public static void EnsureWave(PlanningCommandContext context, MigrationWave wave, bool requireMapping)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wave);
        EnsureSchema(context);
        EnsureVersion(context.ExpectedWaveVersion, wave.Version.Value, "versão de seleção da onda");
        EnsureHash(context.ExpectedSelectionHash, wave.SelectionHash, "hash de seleção da onda");
        EnsureHash(context.ExpectedConfigurationHash, wave.ConfigurationHash, "hash de configuração da onda");
        EnsureText(context.ExpectedTargetRootFolder, wave.TargetRootFolder.Value, "pasta de destino");

        if (requireMapping)
        {
            EnsureVersion(context.MappingSchemaVersion, MappingSchema.Version, "versão do esquema de mapping");
            EnsureVersion(context.MappingGeneratorVersion, MappingCsvGenerator.GeneratorVersion, "versão do gerador de mapping");
        }
    }

    public static void EnsureMappingPolicy(PlanningCommandContext context, MappingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        EnsureVersion(context.MappingPolicyVersion, policy.Version, "versão da política de mapping");
    }

    private static void EnsureVersion(int? expected, int current, string what)
    {
        if (expected is { } value && value != current)
        {
            throw new StaleCommandContextException($"{what} divergente (esperado {value}, atual {current}).");
        }
    }

    private static void EnsureHash(Sha256Hash? expected, Sha256Hash current, string what)
    {
        if (expected is { } value && !string.Equals(value.Value, current.Value, StringComparison.Ordinal))
        {
            throw new StaleCommandContextException($"{what} divergente.");
        }
    }

    private static void EnsureText(string? expected, string current, string what)
    {
        if (expected is not null && !string.Equals(expected, current, StringComparison.Ordinal))
        {
            throw new StaleCommandContextException($"{what} divergente.");
        }
    }
}
