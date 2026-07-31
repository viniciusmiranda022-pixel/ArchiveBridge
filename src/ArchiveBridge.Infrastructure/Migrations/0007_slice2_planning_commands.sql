-- Slice 2 — comandos de planejamento duráveis (integração REAL com os Jobs do Slice 1).
-- Cada linha é o CONTEXTO mínimo e versionado de um comando (sem PII, sem payload pesado), gravado
-- ATOMICAMENTE junto do Job durável (mesma transação): nunca há Job sem operação nem operação sem
-- Job. Um worker reivindica o Job (fencing por época), lê este contexto, executa e conclui/falha.
--   command_type: ValidateProject=0, ValidateWave=1, GenerateMappingCsv=2, FreezeWave=3

CREATE TABLE dbo.planning_commands
(
    job_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id         UNIQUEIDENTIFIER NOT NULL,
    project_id        UNIQUEIDENTIFIER NOT NULL,
    command_type      TINYINT          NOT NULL,
    wave_id           UNIQUEIDENTIFIER NULL,
    content_code_page INT              NULL,
    generated_by      NVARCHAR(200)    NULL,
    correlation_id    UNIQUEIDENTIFIER NOT NULL,
    created_at_utc    DATETIME2(3)     NOT NULL CONSTRAINT DF_planning_commands_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_planning_commands PRIMARY KEY (job_id),
    -- FK composta ao Job (tenant/projeto coerentes): o contexto pertence ao mesmo escopo do Job.
    CONSTRAINT FK_planning_commands_job FOREIGN KEY (job_id, tenant_id, project_id)
        REFERENCES dbo.jobs (job_id, tenant_id, project_id),
    CONSTRAINT CK_planning_commands_type CHECK (command_type BETWEEN 0 AND 3),
    CONSTRAINT CK_planning_commands_codepage
        CHECK (content_code_page IS NULL OR content_code_page BETWEEN 1 AND 65535)
);

CREATE INDEX IX_planning_commands_scope ON dbo.planning_commands (tenant_id, project_id, job_id);

-- A aplicação insere/le o contexto; a manutenção apenas lê (auditoria). Append-only.
GRANT SELECT, INSERT ON dbo.planning_commands TO ab_app_role;
GRANT SELECT ON dbo.planning_commands TO ab_maintenance_role;
GO

-- Isolamento por tenant (defesa em profundidade) na nova tabela.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.planning_commands,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.planning_commands AFTER INSERT;
GO
