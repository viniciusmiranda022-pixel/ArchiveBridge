-- Slice 4A — trilha operacional append-only do Portal.
--
-- Diferente da auditoria de autenticação (que precisa localizar o usuário antes de existir contexto de
-- tenant), esta tabela registra operações já AUTENTICADAS. Portanto participa integralmente da RLS por
-- tenant e carrega project_id explicitamente. A aplicação recebe apenas SELECT/INSERT: sem UPDATE/DELETE.

CREATE TABLE dbo.portal_operational_audit_events
(
    event_id          BIGINT IDENTITY (1, 1) NOT NULL,
    tenant_id         UNIQUEIDENTIFIER NOT NULL,
    project_id        UNIQUEIDENTIFIER NOT NULL,
    user_id           UNIQUEIDENTIFIER NOT NULL,
    username          NVARCHAR(200) NOT NULL,
    action_code       NVARCHAR(64) NOT NULL,
    resource_type     NVARCHAR(64) NOT NULL,
    resource_id       NVARCHAR(200) NOT NULL,
    succeeded         BIT NOT NULL,
    reason            NVARCHAR(100) NOT NULL,
    correlation_id    UNIQUEIDENTIFIER NOT NULL,
    occurred_at_utc   DATETIME2(3) NOT NULL,
    CONSTRAINT PK_portal_operational_audit_events PRIMARY KEY (event_id),
    CONSTRAINT FK_portal_operational_audit_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT FK_portal_operational_audit_user FOREIGN KEY (user_id)
        REFERENCES dbo.portal_users (user_id)
);
GO

CREATE INDEX IX_portal_operational_audit_scope_time
    ON dbo.portal_operational_audit_events (tenant_id, project_id, occurred_at_utc DESC, event_id DESC);
GO

GRANT SELECT, INSERT ON dbo.portal_operational_audit_events TO ab_app_role;
GRANT SELECT ON dbo.portal_operational_audit_events TO ab_maintenance_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.portal_operational_audit_events,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.portal_operational_audit_events AFTER INSERT;
GO
