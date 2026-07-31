-- Vertical Slice 2 — Projetos, ondas de migração, planejamento e mapping (SQL Server on-premises).
-- Persiste APENAS metadados de planejamento/governança: NÃO há conteúdo de e-mail/PST, segredo, SAS
-- nem token nestas tabelas. Enums são persistidos como TINYINT com o MESMO valor numérico do domínio:
--   ProjectStatus:  Draft=0, ReadyForAssessment=1, Blocked=2, Approved=3, Active=4, Completed=5, Cancelled=6
--   TargetArchivePolicy: OnlineArchive=0, PrimaryMailbox=1
--   WaveStatus:     Draft=0, Validating=1, Blocked=2, ReadyForApproval=3, Approved=4, Frozen=5, Completed=6, Cancelled=7
--   CapacityAssessmentResult: WithinLimit=0, AssessmentRequired=1
--   MappingVersionStatus:     Usable=0, Superseded=1
--   MappingValidationOutcome: Passed=0, Failed=1
--   approvals.subject_type: Project=0, Wave=1, Mapping=2 | approvals.decision: Approved=0, Rejected=1
-- Coerência tenant/projeto é garantida no BANCO por chaves compostas (não apenas no código). A
-- imutabilidade de versões/hashes é reforçada por privilégios (0005), constraints e gatilhos (abaixo).

CREATE TABLE dbo.migration_projects
(
    project_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_name          NVARCHAR(200)    NOT NULL,
    project_owner         NVARCHAR(200)    NOT NULL,
    target_tenant         NVARCHAR(256)    NOT NULL,
    target_archive_policy TINYINT          NOT NULL,
    configuration_version INT              NOT NULL,
    configuration_hash    CHAR(64)         NOT NULL,
    status                TINYINT          NOT NULL,
    created_at_utc        DATETIME2(3)     NOT NULL CONSTRAINT DF_migration_projects_created DEFAULT SYSUTCDATETIME(),
    updated_at_utc        DATETIME2(3)     NOT NULL CONSTRAINT DF_migration_projects_updated DEFAULT SYSUTCDATETIME(),
    row_version           ROWVERSION       NOT NULL,
    CONSTRAINT PK_migration_projects PRIMARY KEY (project_id),
    CONSTRAINT UQ_migration_projects_identity UNIQUE (tenant_id, project_id),
    -- Um projeto de migração corresponde ao projeto-base do Slice 1 (integração com Jobs duráveis).
    CONSTRAINT FK_migration_projects_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_migration_projects_status CHECK (status BETWEEN 0 AND 6),
    CONSTRAINT CK_migration_projects_policy CHECK (target_archive_policy BETWEEN 0 AND 1),
    CONSTRAINT CK_migration_projects_cfgver CHECK (configuration_version >= 1)
);

-- Histórico imutável de versões de configuração (append-only; sem UPDATE — ver 0005).
CREATE TABLE dbo.project_configuration_versions
(
    project_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    configuration_version INT              NOT NULL,
    target_tenant         NVARCHAR(256)    NOT NULL,
    target_archive_policy TINYINT          NOT NULL,
    configuration_hash    CHAR(64)         NOT NULL,
    created_at_utc        DATETIME2(3)     NOT NULL CONSTRAINT DF_pcv_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_project_configuration_versions PRIMARY KEY (project_id, configuration_version),
    CONSTRAINT FK_pcv_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.migration_projects (tenant_id, project_id),
    CONSTRAINT CK_pcv_policy CHECK (target_archive_policy BETWEEN 0 AND 1),
    CONSTRAINT CK_pcv_version CHECK (configuration_version >= 1),
    -- O mesmo par (versão, hash) é determinístico: a versão fixa um hash específico.
    CONSTRAINT UQ_pcv_version_hash UNIQUE (project_id, configuration_version, configuration_hash)
);

CREATE TABLE dbo.migration_waves
(
    wave_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id          UNIQUEIDENTIFIER NOT NULL,
    project_id         UNIQUEIDENTIFIER NOT NULL,
    wave_version       INT              NOT NULL,
    name               NVARCHAR(200)    NOT NULL,
    status             TINYINT          NOT NULL,
    target_root_folder NVARCHAR(400)    NOT NULL,
    planned_bytes      BIGINT           NOT NULL,
    planned_items      BIGINT           NOT NULL,
    configuration_hash CHAR(64)         NOT NULL,
    selection_hash     CHAR(64)         NOT NULL,
    approved_at_utc    DATETIME2(3)     NULL,
    approved_by        NVARCHAR(200)    NULL,
    created_at_utc     DATETIME2(3)     NOT NULL CONSTRAINT DF_migration_waves_created DEFAULT SYSUTCDATETIME(),
    row_version        ROWVERSION       NOT NULL,
    CONSTRAINT PK_migration_waves PRIMARY KEY (wave_id),
    CONSTRAINT UQ_migration_waves_identity UNIQUE (wave_id, tenant_id, project_id),
    CONSTRAINT FK_migration_waves_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.migration_projects (tenant_id, project_id),
    -- A pasta de destino nunca é reutilizada por outra onda/projeto — unicidade GLOBAL (mesmo sob
    -- RLS, a constraint enxerga todas as linhas), impedindo reuso cross-projeto/cross-tenant.
    CONSTRAINT UQ_migration_waves_target_root UNIQUE (target_root_folder),
    CONSTRAINT CK_migration_waves_status CHECK (status BETWEEN 0 AND 7),
    CONSTRAINT CK_migration_waves_wavever CHECK (wave_version >= 1),
    CONSTRAINT CK_migration_waves_planned CHECK (planned_bytes >= 0 AND planned_items >= 0),
    -- Não há aprovação anônima: Approved/Frozen/Completed exigem responsável e data.
    CONSTRAINT CK_migration_waves_approval CHECK (
        (status < 4 AND approved_at_utc IS NULL AND approved_by IS NULL)
        OR (status IN (4, 5, 6) AND approved_at_utc IS NOT NULL AND approved_by IS NOT NULL)
        OR (status = 7))
);

CREATE INDEX IX_migration_waves_project ON dbo.migration_waves (tenant_id, project_id, status);

-- Histórico imutável de versões de seleção da onda (append-only).
CREATE TABLE dbo.wave_versions
(
    wave_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id          UNIQUEIDENTIFIER NOT NULL,
    project_id         UNIQUEIDENTIFIER NOT NULL,
    wave_version       INT              NOT NULL,
    selection_hash     CHAR(64)         NOT NULL,
    configuration_hash CHAR(64)         NOT NULL,
    target_root_folder NVARCHAR(400)    NOT NULL,
    planned_bytes      BIGINT           NOT NULL,
    planned_items      BIGINT           NOT NULL,
    created_at_utc     DATETIME2(3)     NOT NULL CONSTRAINT DF_wave_versions_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_wave_versions PRIMARY KEY (wave_id, wave_version),
    CONSTRAINT FK_wave_versions_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    -- Alvo da FK composta de wave_entries: garante coerência de tenant/projeto entre pai e filho.
    CONSTRAINT UQ_wave_versions_scope UNIQUE (wave_id, wave_version, tenant_id, project_id),
    CONSTRAINT CK_wave_versions_version CHECK (wave_version >= 1),
    CONSTRAINT CK_wave_versions_planned CHECK (planned_bytes >= 0 AND planned_items >= 0)
);

-- Snapshot imutável das entradas (PSTs) por versão de seleção. Metadados de planejamento — não é
-- conteúdo de e-mail/PST. Nome de PST é único por versão (fail-closed; sem duplicação).
CREATE TABLE dbo.wave_entries
(
    entry_id     BIGINT IDENTITY (1, 1) NOT NULL,
    wave_id      UNIQUEIDENTIFIER       NOT NULL,
    tenant_id    UNIQUEIDENTIFIER       NOT NULL,
    project_id   UNIQUEIDENTIFIER       NOT NULL,
    wave_version INT                    NOT NULL,
    file_path         NVARCHAR(400)     NOT NULL,
    pst_name          NVARCHAR(260)     NOT NULL,
    mailbox           NVARCHAR(320)     NOT NULL,
    -- Identidade canônica (resolvida) do archive: a chave de agrupamento de capacidade (a mailbox é
    -- apenas metadado de exibição). Persistida para preservar a resolução de aliases entre recargas.
    target_archive_id NVARCHAR(320)     NOT NULL,
    size_bytes        BIGINT            NOT NULL,
    item_count        BIGINT            NOT NULL,
    CONSTRAINT PK_wave_entries PRIMARY KEY (entry_id),
    -- FK composta com tenant_id/project_id: uma entrada NÃO pode referenciar uma versão de outro
    -- tenant/projeto mesmo conhecendo o (wave_id, wave_version). Coerência garantida no banco.
    CONSTRAINT FK_wave_entries_version FOREIGN KEY (wave_id, wave_version, tenant_id, project_id)
        REFERENCES dbo.wave_versions (wave_id, wave_version, tenant_id, project_id),
    CONSTRAINT UQ_wave_entries_pst UNIQUE (wave_id, wave_version, pst_name),
    CONSTRAINT CK_wave_entries_size CHECK (size_bytes >= 0 AND item_count >= 0)
);

CREATE INDEX IX_wave_entries_scope ON dbo.wave_entries (tenant_id, project_id, wave_id, wave_version);

-- Evidência de cada geração de mapping. content_sha256 é o mapping.sha256. Uma nova geração cria
-- N+1 e marca a anterior como Superseded (status=1) — evidência preservada, nunca sobrescrita.
CREATE TABLE dbo.mapping_csv_versions
(
    wave_id            UNIQUEIDENTIFIER NOT NULL,
    mapping_version    INT              NOT NULL,
    tenant_id          UNIQUEIDENTIFIER NOT NULL,
    project_id         UNIQUEIDENTIFIER NOT NULL,
    configuration_hash CHAR(64)         NOT NULL,
    selection_hash     CHAR(64)         NOT NULL,
    content_sha256     CHAR(64)         NOT NULL,
    row_count          INT              NOT NULL,
    validation_result  TINYINT          NOT NULL,
    generated_by       NVARCHAR(200)    NOT NULL,
    created_at_utc     DATETIME2(3)     NOT NULL CONSTRAINT DF_mcv_created DEFAULT SYSUTCDATETIME(),
    status             TINYINT          NOT NULL,
    CONSTRAINT PK_mapping_csv_versions PRIMARY KEY (wave_id, mapping_version),
    CONSTRAINT FK_mcv_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    -- Alvo da FK composta de mapping_csv_rows (coerência de tenant/projeto entre pai e filho).
    CONSTRAINT UQ_mcv_scope UNIQUE (wave_id, mapping_version, tenant_id, project_id),
    CONSTRAINT CK_mcv_status CHECK (status BETWEEN 0 AND 1),
    CONSTRAINT CK_mcv_validation CHECK (validation_result BETWEEN 0 AND 1),
    CONSTRAINT CK_mcv_rowcount CHECK (row_count >= 0 AND row_count <= 500),
    CONSTRAINT CK_mcv_version CHECK (mapping_version >= 1)
);

-- No máximo UMA versão utilizável por onda; as demais permanecem como evidência (Superseded).
CREATE UNIQUE INDEX UX_mcv_single_usable ON dbo.mapping_csv_versions (wave_id) WHERE status = 0;
CREATE INDEX IX_mcv_scope ON dbo.mapping_csv_versions (tenant_id, project_id, wave_id, mapping_version);

-- Linhas do CSV de evidência (metadados; SharePoint sempre vazio; workload/is_archive fixos).
CREATE TABLE dbo.mapping_csv_rows
(
    row_id                BIGINT IDENTITY (1, 1) NOT NULL,
    wave_id               UNIQUEIDENTIFIER       NOT NULL,
    mapping_version       INT                    NOT NULL,
    tenant_id             UNIQUEIDENTIFIER       NOT NULL,
    project_id            UNIQUEIDENTIFIER       NOT NULL,
    row_number            INT                    NOT NULL,
    workload              NVARCHAR(50)           NOT NULL,
    file_path             NVARCHAR(400)          NOT NULL,
    name                  NVARCHAR(260)          NOT NULL,
    mailbox               NVARCHAR(320)          NOT NULL,
    is_archive            NVARCHAR(10)           NOT NULL,
    target_root_folder    NVARCHAR(400)          NOT NULL,
    content_code_page     INT                    NOT NULL,
    sp_file_container     NVARCHAR(400)          NOT NULL CONSTRAINT DF_mcr_spfile DEFAULT N'',
    sp_manifest_container NVARCHAR(400)          NOT NULL CONSTRAINT DF_mcr_spmanifest DEFAULT N'',
    sp_site_url           NVARCHAR(400)          NOT NULL CONSTRAINT DF_mcr_spsite DEFAULT N'',
    CONSTRAINT PK_mapping_csv_rows PRIMARY KEY (row_id),
    -- FK composta com tenant_id/project_id: uma linha NÃO pode referenciar uma versão de mapping de
    -- outro tenant/projeto mesmo conhecendo o (wave_id, mapping_version).
    CONSTRAINT FK_mcr_version FOREIGN KEY (wave_id, mapping_version, tenant_id, project_id)
        REFERENCES dbo.mapping_csv_versions (wave_id, mapping_version, tenant_id, project_id),
    CONSTRAINT UQ_mcr_name UNIQUE (wave_id, mapping_version, name),
    CONSTRAINT CK_mcr_sp_empty CHECK (sp_file_container = N'' AND sp_manifest_container = N'' AND sp_site_url = N''),
    CONSTRAINT CK_mcr_workload CHECK (workload = N'Exchange'),
    CONSTRAINT CK_mcr_isarchive CHECK (is_archive = N'TRUE'),
    CONSTRAINT CK_mcr_codepage CHECK (content_code_page BETWEEN 1 AND 65535)
);

CREATE INDEX IX_mcr_scope ON dbo.mapping_csv_rows (tenant_id, project_id, wave_id, mapping_version, row_number);

-- Registro de avaliação de capacidade (append-only): volume por archive, regra, resultado, motivo,
-- correlação, data e responsável pela liberação. Sem conteúdo/segredo.
CREATE TABLE dbo.planning_assessments
(
    assessment_id   BIGINT IDENTITY (1, 1) NOT NULL,
    tenant_id       UNIQUEIDENTIFIER       NOT NULL,
    project_id      UNIQUEIDENTIFIER       NOT NULL,
    wave_id           UNIQUEIDENTIFIER     NOT NULL,
    wave_version      INT                  NOT NULL,
    -- Identidade canônica do archive avaliado (a soma de capacidade é por esta chave, não pela
    -- string de exibição da mailbox).
    target_archive_id NVARCHAR(320)        NOT NULL,
    total_bytes       BIGINT               NOT NULL,
    rule_code         NVARCHAR(64)         NOT NULL,
    result          TINYINT                NOT NULL,
    reason          NVARCHAR(400)          NOT NULL,
    correlation_id  UNIQUEIDENTIFIER       NOT NULL,
    assessed_at_utc DATETIME2(3)           NOT NULL CONSTRAINT DF_pa_assessed DEFAULT SYSUTCDATETIME(),
    released_by     NVARCHAR(200)          NULL,
    CONSTRAINT PK_planning_assessments PRIMARY KEY (assessment_id),
    CONSTRAINT FK_pa_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    CONSTRAINT CK_pa_result CHECK (result BETWEEN 0 AND 1),
    CONSTRAINT CK_pa_total CHECK (total_bytes >= 0)
);

CREATE INDEX IX_pa_scope ON dbo.planning_assessments (tenant_id, project_id, wave_id, wave_version);

-- Registro imutável de decisões de aprovação (append-only): quem, quando, sobre o quê e qual versão.
CREATE TABLE dbo.approvals
(
    approval_id     BIGINT IDENTITY (1, 1) NOT NULL,
    tenant_id       UNIQUEIDENTIFIER       NOT NULL,
    project_id      UNIQUEIDENTIFIER       NOT NULL,
    subject_type    TINYINT                NOT NULL,
    subject_id      UNIQUEIDENTIFIER       NOT NULL,
    subject_version INT                    NOT NULL,
    decision        TINYINT                NOT NULL,
    approved_by     NVARCHAR(200)          NOT NULL,
    approved_at_utc DATETIME2(3)           NOT NULL CONSTRAINT DF_approvals_at DEFAULT SYSUTCDATETIME(),
    correlation_id  UNIQUEIDENTIFIER       NOT NULL,
    notes           NVARCHAR(400)          NULL,
    CONSTRAINT PK_approvals PRIMARY KEY (approval_id),
    CONSTRAINT FK_approvals_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.migration_projects (tenant_id, project_id),
    CONSTRAINT CK_approvals_subject_type CHECK (subject_type BETWEEN 0 AND 2),
    CONSTRAINT CK_approvals_decision CHECK (decision BETWEEN 0 AND 1),
    CONSTRAINT CK_approvals_version CHECK (subject_version >= 1)
);

CREATE INDEX IX_approvals_subject ON dbo.approvals (tenant_id, project_id, subject_type, subject_id, subject_version);
GO

-- Congelamento de configuração de projeto: uma vez Approved (status>=3), hash/versão/destino não
-- podem mudar. Reforça no BANCO a invariante "configuração aprovada não muda silenciosamente".
CREATE TRIGGER dbo.TR_migration_projects_freeze_config
    ON dbo.migration_projects
    AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(configuration_hash) OR UPDATE(configuration_version)
        OR UPDATE(target_tenant) OR UPDATE(target_archive_policy)
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM deleted AS d
                INNER JOIN inserted AS i ON i.project_id = d.project_id
            WHERE d.status >= 3
              AND (i.configuration_hash <> d.configuration_hash
                  OR i.configuration_version <> d.configuration_version
                  OR i.target_tenant <> d.target_tenant
                  OR i.target_archive_policy <> d.target_archive_policy))
        BEGIN
            THROW 50010, 'Configuração de projeto congelada (Approved+): não pode ser alterada.', 1;
        END
    END
END
GO

-- Congelamento de onda: uma vez Approved (status>=4), seleção e destino não podem mudar. Reforça no
-- BANCO a invariante "seleção/destino aprovados são imutáveis" (retry preserva o mesmo destino).
CREATE TRIGGER dbo.TR_migration_waves_freeze
    ON dbo.migration_waves
    AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(target_root_folder) OR UPDATE(selection_hash)
        OR UPDATE(configuration_hash) OR UPDATE(wave_version)
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM deleted AS d
                INNER JOIN inserted AS i ON i.wave_id = d.wave_id
            WHERE d.status >= 4
              AND (i.target_root_folder <> d.target_root_folder
                  OR i.selection_hash <> d.selection_hash
                  OR i.configuration_hash <> d.configuration_hash
                  OR i.wave_version <> d.wave_version))
        BEGIN
            THROW 50011, 'Onda aprovada/congelada: seleção e destino não podem ser alterados.', 1;
        END
    END
END
GO
