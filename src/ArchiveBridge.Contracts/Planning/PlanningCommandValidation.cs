namespace ArchiveBridge.Contracts.Planning;

/// <summary>
/// Validação fail-closed do contexto de um comando durável POR TIPO. Espelha, no código, a
/// constraint <c>CK_pc_required_by_type</c> do banco: cada <see cref="PlanningCommandKind"/> exige um
/// conjunto mínimo de campos (versão/hash de configuração, versão/hash de seleção, pasta de destino,
/// e — para mapping — code page, autor e versões de esquema/gerador/política). Um campo obrigatório
/// ausente é recusado antes de enfileirar e ao processar, nunca ignorado silenciosamente.
/// </summary>
public static class PlanningCommandValidation
{
    /// <summary>Garante que o comando tem o contexto obrigatório para o seu tipo; lança se faltar campo.</summary>
    public static void EnsureValid(PlanningCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var context = command.Context;
        if (context.SchemaVersion != PlanningCommandContext.CurrentSchemaVersion)
        {
            throw new ArgumentException(
                $"Schema de comando desconhecido ({context.SchemaVersion}).", nameof(command));
        }

        switch (command.Kind)
        {
            case PlanningCommandKind.ValidateProject:
                Require(context.ExpectedProjectConfigurationVersion is not null, "expected_project_configuration_version");
                Require(context.ExpectedConfigurationHash is not null, "expected_configuration_hash");
                break;
            case PlanningCommandKind.ValidateWave:
            case PlanningCommandKind.FreezeWave:
                RequireWave(command);
                break;
            case PlanningCommandKind.GenerateMappingCsv:
                RequireWave(command);
                Require(command.ContentCodePage is not null, "content_code_page");
                Require(!string.IsNullOrWhiteSpace(command.GeneratedBy), "generated_by");
                Require(context.MappingSchemaVersion is not null, "mapping_schema_version");
                Require(context.MappingGeneratorVersion is not null, "mapping_generator_version");
                Require(context.MappingPolicyVersion is not null, "mapping_policy_version");
                break;
            default:
                throw new ArgumentException($"Tipo de comando de planejamento desconhecido: {command.Kind}.", nameof(command));
        }
    }

    private static void RequireWave(PlanningCommand command)
    {
        var context = command.Context;
        Require(command.Wave is not null, "wave_id");
        Require(context.ExpectedConfigurationHash is not null, "expected_configuration_hash");
        Require(context.ExpectedWaveVersion is not null, "expected_wave_version");
        Require(context.ExpectedSelectionHash is not null, "expected_selection_hash");
        Require(!string.IsNullOrWhiteSpace(context.ExpectedTargetRootFolder), "expected_target_root_folder");
    }

    private static void Require(bool present, string field)
    {
        if (!present)
        {
            throw new ArgumentException($"Contexto de comando incompleto: campo obrigatório ausente ({field}).", field);
        }
    }
}
