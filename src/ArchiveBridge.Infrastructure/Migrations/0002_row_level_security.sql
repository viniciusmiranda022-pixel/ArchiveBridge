-- Row-Level Security por tenant (defesa em profundidade, camada de banco).
-- O predicado permite uma linha quando o tenant_id da linha é igual ao tenant do
-- SESSION_CONTEXT('tenant_id') OU quando a sessão está em modo de manutenção controlado
-- (SESSION_CONTEXT('is_maintenance')=1), usado apenas pelo reaper de leases expirados.
-- Sem tenant e sem manutenção => nenhuma linha é visível e toda escrita é bloqueada (fail-closed).

IF SCHEMA_ID(N'rls') IS NULL
    EXEC (N'CREATE SCHEMA rls');
GO

CREATE OR ALTER FUNCTION rls.fn_tenant_access(@tenant_id UNIQUEIDENTIFIER)
    RETURNS TABLE
    WITH SCHEMABINDING
AS
    RETURN
        SELECT 1 AS is_allowed
        WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS UNIQUEIDENTIFIER)
           OR CAST(SESSION_CONTEXT(N'is_maintenance') AS BIT) = 1;
GO

CREATE SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.projects,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.projects AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.jobs,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.jobs AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.jobs AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.job_attempts,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.job_attempts AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.job_state_transitions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.job_state_transitions AFTER INSERT
    WITH (STATE = ON);
GO
