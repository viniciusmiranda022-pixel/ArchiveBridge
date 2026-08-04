-- Slice 2 — estende a política de isolamento por tenant às novas tabelas (defesa em profundidade,
-- camada de banco). Reutiliza rls.fn_tenant_access (0003): a linha é permitida quando seu tenant_id
-- é igual ao SESSION_CONTEXT('tenant_id') OU quando a sessão é manutenção autorizada
-- (is_maintenance=1 E membro de ab_maintenance_role). Sem tenant e sem manutenção autorizada =>
-- nenhuma linha visível e toda escrita bloqueada (fail-closed). O isolamento POR PROJETO é reforçado
-- pelo filtro explícito por project_id nas consultas e pelas chaves compostas (0004).

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_projects,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_projects AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_projects AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.project_configuration_versions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.project_configuration_versions AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_waves,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_waves AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_waves AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.wave_versions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.wave_versions AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.wave_entries,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.wave_entries AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_csv_versions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_csv_versions AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_csv_versions AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_csv_rows,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_csv_rows AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.planning_assessments,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.planning_assessments AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.approvals,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.approvals AFTER INSERT;
GO
