-- Slice 3 — Enterprise Vault Capability Discovery (read-only). Migração ADITIVA e protegida por hash:
-- só cria objetos novos, sem alterar migrations já aplicadas e sem operação destrutiva. Persiste APENAS
-- metadados de governança/descoberta (versão, status, hashes, adapter, caminho lógico, timestamps): nada
-- de credencial, segredo, token, conteúdo de mensagem/PST. A evidência detalhada é um artefato imutável
-- fora do SQL (filesystem on-premises versionado); aqui ficam só metadados + hashes + caminho lógico.
--   status (EvDiscoveryStatus): Pending=0, Discovering=1, Ready=2, Blocked=3, Unsupported=4, Failed=5, Superseded=6
--   availability (CapabilityAvailability): Available=0, Unavailable=1, Indeterminate=2, PermissionDenied=3, Unsupported=4
--   compatibility (AdapterCompatibility): Supported=0, Blocked=1, Unsupported=2

-- Ambientes EV registrados (alvo da descoberta). Nomes técnicos de site/servidor são informação sensível
-- de infraestrutura (nunca vão para log); ficam sob RLS por tenant + isolamento por projeto.
CREATE TABLE dbo.ev_environments
(
    environment_id   UNIQUEIDENTIFIER NOT NULL,
    tenant_id        UNIQUEIDENTIFIER NOT NULL,
    project_id       UNIQUEIDENTIFIER NOT NULL,
    site_name        NVARCHAR(200)    NOT NULL,
    directory_server NVARCHAR(260)    NOT NULL,
    created_at_utc   DATETIME2(3)     NOT NULL CONSTRAINT DF_ev_environments_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ev_environments PRIMARY KEY (environment_id),
    -- Alvo da FK composta dos runs (coerência de tenant/projeto entre pai e filho).
    CONSTRAINT UQ_ev_environments_scope UNIQUE (environment_id, tenant_id, project_id)
);

CREATE INDEX IX_ev_environments_scope ON dbo.ev_environments (tenant_id, project_id, environment_id);
GO

-- Comandos duráveis de descoberta (contexto mínimo e versionado, sem credenciais). Gravado ATOMICAMENTE
-- junto do Job durável (workload EnterpriseVault=1) — o claim é EXCLUSIVO por EXISTS neste registro. O
-- contexto obrigatório (versão/hash de config do projeto) é NOT NULL: um comando incompleto não é aceito.
CREATE TABLE dbo.ev_discovery_commands
(
    job_id                                 UNIQUEIDENTIFIER NOT NULL,
    tenant_id                              UNIQUEIDENTIFIER NOT NULL,
    project_id                             UNIQUEIDENTIFIER NOT NULL,
    environment_id                         UNIQUEIDENTIFIER NOT NULL,
    site_name                              NVARCHAR(200)    NOT NULL,
    directory_server                       NVARCHAR(260)    NOT NULL,
    requested_by                           NVARCHAR(200)    NOT NULL,
    command_schema_version                 INT              NOT NULL,
    expected_project_configuration_version INT              NOT NULL,
    expected_configuration_hash            CHAR(64)         NOT NULL,
    discovery_policy_version               INT              NOT NULL,
    correlation_id                         UNIQUEIDENTIFIER NOT NULL,
    created_at_utc                         DATETIME2(3)     NOT NULL CONSTRAINT DF_evdc_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ev_discovery_commands PRIMARY KEY (job_id),
    CONSTRAINT FK_ev_discovery_commands_job FOREIGN KEY (job_id, tenant_id, project_id)
        REFERENCES dbo.jobs (job_id, tenant_id, project_id),
    CONSTRAINT CK_evdc_schema CHECK (command_schema_version >= 1),
    CONSTRAINT CK_evdc_policy CHECK (discovery_policy_version >= 1)
);

CREATE INDEX IX_evdc_scope ON dbo.ev_discovery_commands (tenant_id, project_id, job_id);
GO

-- Runs de descoberta (âncora de versão + ciclo de vida). Uma nova descoberta cria N+1 como Pending e só
-- promove a terminal (Ready/Blocked/Unsupported/Failed) após a evidência publicada e validada; a anterior
-- vira Superseded somente então. O índice único filtrado garante NO MÁXIMO uma versão "corrente" por
-- ambiente. Só a coluna status pode ser atualizada pela aplicação (hashes/evidência imutáveis).
CREATE TABLE dbo.ev_discovery_runs
(
    environment_id           UNIQUEIDENTIFIER NOT NULL,
    discovery_version        INT              NOT NULL,
    tenant_id                UNIQUEIDENTIFIER NOT NULL,
    project_id               UNIQUEIDENTIFIER NOT NULL,
    run_id                   UNIQUEIDENTIFIER NOT NULL,
    status                   TINYINT          NOT NULL,
    result_status            TINYINT          NOT NULL,
    result_code              NVARCHAR(64)     NOT NULL,
    selected_adapter         NVARCHAR(200)    NULL,
    adapter_version          INT              NULL,
    observed_version         NVARCHAR(100)    NOT NULL,
    discovery_schema_version INT              NOT NULL,
    configuration_hash       CHAR(64)         NOT NULL,
    evidence_hash            CHAR(64)         NOT NULL,
    evidence_path            NVARCHAR(400)    NOT NULL,
    evidence_size_bytes      BIGINT           NOT NULL,
    requested_by             NVARCHAR(200)    NULL,
    worker_id                NVARCHAR(200)    NULL,
    correlation_id           UNIQUEIDENTIFIER NOT NULL,
    started_at_utc           DATETIME2(3)     NOT NULL,
    completed_at_utc         DATETIME2(3)     NOT NULL,
    created_at_utc           DATETIME2(3)     NOT NULL CONSTRAINT DF_evd_created DEFAULT SYSUTCDATETIME(),
    row_version              ROWVERSION       NOT NULL,
    CONSTRAINT PK_ev_discovery_runs PRIMARY KEY (environment_id, discovery_version),
    CONSTRAINT UQ_evd_scope UNIQUE (environment_id, discovery_version, tenant_id, project_id),
    CONSTRAINT FK_evd_environment FOREIGN KEY (environment_id, tenant_id, project_id)
        REFERENCES dbo.ev_environments (environment_id, tenant_id, project_id),
    CONSTRAINT CK_evd_status CHECK (status BETWEEN 0 AND 6),
    CONSTRAINT CK_evd_result_status CHECK (result_status BETWEEN 2 AND 5),
    CONSTRAINT CK_evd_version CHECK (discovery_version >= 1),
    CONSTRAINT CK_evd_size CHECK (evidence_size_bytes >= 0)
);

-- No máximo UMA versão corrente por ambiente; Pending (0) e Superseded (6) não contam (reconciliáveis).
CREATE UNIQUE INDEX UX_evd_single_current ON dbo.ev_discovery_runs (environment_id) WHERE status IN (2, 3, 4, 5);
CREATE INDEX IX_evd_scope ON dbo.ev_discovery_runs (tenant_id, project_id, environment_id, discovery_version);
CREATE INDEX IX_evd_hashes ON dbo.ev_discovery_runs (environment_id, configuration_hash, evidence_hash);
GO

-- Capacidades descobertas por run (append-only). Uma capacidade Available exige evidência positiva.
CREATE TABLE dbo.ev_capabilities
(
    capability_row_id  BIGINT IDENTITY (1, 1) NOT NULL,
    environment_id     UNIQUEIDENTIFIER       NOT NULL,
    discovery_version  INT                    NOT NULL,
    tenant_id          UNIQUEIDENTIFIER       NOT NULL,
    project_id         UNIQUEIDENTIFIER       NOT NULL,
    capability_code    NVARCHAR(64)           NOT NULL,
    capability_version INT                    NOT NULL,
    availability       TINYINT                NOT NULL,
    evidence_reference NVARCHAR(200)          NOT NULL,
    blocking_reason    NVARCHAR(400)          NULL,
    CONSTRAINT PK_ev_capabilities PRIMARY KEY (capability_row_id),
    CONSTRAINT FK_evcap_run FOREIGN KEY (environment_id, discovery_version, tenant_id, project_id)
        REFERENCES dbo.ev_discovery_runs (environment_id, discovery_version, tenant_id, project_id),
    CONSTRAINT UQ_evcap_code UNIQUE (environment_id, discovery_version, capability_code),
    CONSTRAINT CK_evcap_availability CHECK (availability BETWEEN 0 AND 4)
);

CREATE INDEX IX_evcap_scope ON dbo.ev_capabilities (tenant_id, project_id, environment_id, discovery_version);
GO

-- Avaliações de adapter consideradas por run (append-only): candidatos, compatibilidade e precedência.
CREATE TABLE dbo.ev_adapter_evaluations
(
    evaluation_id     BIGINT IDENTITY (1, 1) NOT NULL,
    environment_id    UNIQUEIDENTIFIER       NOT NULL,
    discovery_version INT                    NOT NULL,
    tenant_id         UNIQUEIDENTIFIER       NOT NULL,
    project_id        UNIQUEIDENTIFIER       NOT NULL,
    adapter_id        NVARCHAR(200)          NOT NULL,
    adapter_version   INT                    NOT NULL,
    compatibility     TINYINT                NOT NULL,
    precedence        INT                    NOT NULL,
    CONSTRAINT PK_ev_adapter_evaluations PRIMARY KEY (evaluation_id),
    CONSTRAINT FK_eveval_run FOREIGN KEY (environment_id, discovery_version, tenant_id, project_id)
        REFERENCES dbo.ev_discovery_runs (environment_id, discovery_version, tenant_id, project_id),
    CONSTRAINT UQ_eveval_adapter UNIQUE (environment_id, discovery_version, adapter_id),
    CONSTRAINT CK_eveval_compat CHECK (compatibility BETWEEN 0 AND 2)
);

CREATE INDEX IX_eveval_scope ON dbo.ev_adapter_evaluations (tenant_id, project_id, environment_id, discovery_version);
GO

-- Achados de bloqueio por run (append-only, estruturados por result_code — nunca por mensagem textual).
CREATE TABLE dbo.ev_discovery_findings
(
    finding_id        BIGINT IDENTITY (1, 1) NOT NULL,
    environment_id    UNIQUEIDENTIFIER       NOT NULL,
    discovery_version INT                    NOT NULL,
    tenant_id         UNIQUEIDENTIFIER       NOT NULL,
    project_id        UNIQUEIDENTIFIER       NOT NULL,
    result_code       NVARCHAR(64)           NOT NULL,
    capability_code   NVARCHAR(64)           NULL,
    reason            NVARCHAR(400)          NOT NULL,
    discovered_at_utc DATETIME2(3)           NOT NULL,
    CONSTRAINT PK_ev_discovery_findings PRIMARY KEY (finding_id),
    CONSTRAINT FK_evfind_run FOREIGN KEY (environment_id, discovery_version, tenant_id, project_id)
        REFERENCES dbo.ev_discovery_runs (environment_id, discovery_version, tenant_id, project_id)
);

CREATE INDEX IX_evfind_scope ON dbo.ev_discovery_findings (tenant_id, project_id, environment_id, discovery_version);
GO

-- Projeções lógicas (o conjunto de capacidades e a evidência) sobre a âncora de runs — herdam a RLS das
-- tabelas base. Mantêm os nomes lógicos do modelo sem duplicar escrita nem estado.
CREATE VIEW dbo.ev_capability_sets
AS
SELECT environment_id, discovery_version, tenant_id, project_id, selected_adapter AS adapter_id,
       adapter_version, discovery_schema_version, configuration_hash, evidence_hash, status
FROM dbo.ev_discovery_runs;
GO

CREATE VIEW dbo.ev_discovery_evidence
AS
SELECT environment_id, discovery_version, tenant_id, project_id, run_id, evidence_path AS evidence_logical_path,
       evidence_size_bytes, evidence_hash, configuration_hash, result_code, status
FROM dbo.ev_discovery_runs;
GO

-- Privilégios: a aplicação escreve o ciclo de descoberta; a manutenção só lê (auditoria). Append-only nas
-- tabelas de evidência; em ev_discovery_runs o único UPDATE permitido é da coluna status (promover/superseder).
GRANT SELECT, INSERT ON dbo.ev_environments TO ab_app_role;
GRANT SELECT ON dbo.ev_environments TO ab_maintenance_role;
GRANT SELECT, INSERT ON dbo.ev_discovery_commands TO ab_app_role;
GRANT SELECT ON dbo.ev_discovery_commands TO ab_maintenance_role;
GRANT SELECT, INSERT ON dbo.ev_discovery_runs TO ab_app_role;
GRANT UPDATE (status) ON dbo.ev_discovery_runs TO ab_app_role;
GRANT SELECT ON dbo.ev_discovery_runs TO ab_maintenance_role;
GRANT SELECT, INSERT ON dbo.ev_capabilities TO ab_app_role;
GRANT SELECT ON dbo.ev_capabilities TO ab_maintenance_role;
GRANT SELECT, INSERT ON dbo.ev_adapter_evaluations TO ab_app_role;
GRANT SELECT ON dbo.ev_adapter_evaluations TO ab_maintenance_role;
GRANT SELECT, INSERT ON dbo.ev_discovery_findings TO ab_app_role;
GRANT SELECT ON dbo.ev_discovery_findings TO ab_maintenance_role;
GRANT SELECT ON dbo.ev_capability_sets TO ab_app_role;
GRANT SELECT ON dbo.ev_capability_sets TO ab_maintenance_role;
GRANT SELECT ON dbo.ev_discovery_evidence TO ab_app_role;
GRANT SELECT ON dbo.ev_discovery_evidence TO ab_maintenance_role;
GO

-- Isolamento por tenant (defesa em profundidade) nas novas tabelas base. O isolamento POR PROJETO é
-- reforçado pelo filtro explícito por project_id e pelas FKs/índices compostos.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_environments,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_environments AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_discovery_commands,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_discovery_commands AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_discovery_runs,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_discovery_runs AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_discovery_runs AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_capabilities,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_capabilities AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_adapter_evaluations,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_adapter_evaluations AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_discovery_findings,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_discovery_findings AFTER INSERT;
GO
