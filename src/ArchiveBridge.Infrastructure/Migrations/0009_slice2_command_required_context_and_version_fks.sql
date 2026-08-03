-- Slice 2 (3ª revisão técnica) — endurecimento SEM alterar migrations já aplicadas (só adiciona
-- constraints/objetos; sem alteração destrutiva). Persiste APENAS metadados de planejamento.

-- B2 — Contexto OBRIGATÓRIO por tipo de comando (fail-closed no BANCO, além do código):
--   command_type: ValidateProject=0, ValidateWave=1, GenerateMappingCsv=2, FreezeWave=3
-- ValidateProject exige config; ValidateWave/FreezeWave exigem onda+seleção+destino;
-- GenerateMappingCsv exige todos os campos de onda + code page + generated_by + versões do mapping.
ALTER TABLE dbo.planning_commands WITH CHECK ADD CONSTRAINT CK_pc_required_by_type CHECK (
    (command_type = 0
        AND expected_project_configuration_version IS NOT NULL
        AND expected_configuration_hash IS NOT NULL)
 OR (command_type IN (1, 3)
        AND wave_id IS NOT NULL
        AND expected_configuration_hash IS NOT NULL
        AND expected_wave_version IS NOT NULL
        AND expected_selection_hash IS NOT NULL
        AND expected_target_root_folder IS NOT NULL)
 OR (command_type = 2
        AND wave_id IS NOT NULL
        AND expected_configuration_hash IS NOT NULL
        AND expected_wave_version IS NOT NULL
        AND expected_selection_hash IS NOT NULL
        AND expected_target_root_folder IS NOT NULL
        AND content_code_page IS NOT NULL
        AND generated_by IS NOT NULL
        AND mapping_schema_version IS NOT NULL
        AND mapping_generator_version IS NOT NULL
        AND mapping_policy_version IS NOT NULL)
);
GO

-- B5 — Integridade de VERSÃO no banco. Uma avaliação/decisão não pode declarar uma versão de onda
-- que não exista para o MESMO tenant/projeto: FK composta para wave_versions (cujo
-- UQ_wave_versions_scope garante a coerência de escopo). Cross-tenant/projeto/versão inexistente
-- falham no banco, não só no código.
ALTER TABLE dbo.planning_assessments WITH CHECK ADD CONSTRAINT FK_pa_wave_version
    FOREIGN KEY (wave_id, wave_version, tenant_id, project_id)
    REFERENCES dbo.wave_versions (wave_id, wave_version, tenant_id, project_id);
GO

ALTER TABLE dbo.capacity_assessment_decisions WITH CHECK ADD CONSTRAINT FK_cad_wave_version
    FOREIGN KEY (wave_id, wave_version, tenant_id, project_id)
    REFERENCES dbo.wave_versions (wave_id, wave_version, tenant_id, project_id);
GO
