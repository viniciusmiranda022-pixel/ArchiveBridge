-- Slice 4A (Passo 6A) — custódia durável das TENTATIVAS de validação de upload de CSV de mapping.
--
-- Aditiva, append-only e não destrutiva: cria duas tabelas novas (tentativas + problemas), sem DROP, sem
-- alterar dados existentes e sem redefinir as tabelas de mapping/wave atuais. Não altera 0001–0017.
--
-- Conceito distinto de mapping_csv_versions (versões GERADAS/artefatos): aqui persiste-se a RECEPÇÃO — o
-- SHA-256 dos bytes exatos, os metadados autorizados, o snapshot da onda, o desfecho e os problemas. A
-- aplicação recebe apenas SELECT/INSERT (append-only). A identidade de manutenção NÃO tem grant algum.

CREATE TABLE dbo.mapping_validation_attempts
(
    validation_id          UNIQUEIDENTIFIER NOT NULL,
    tenant_id              UNIQUEIDENTIFIER NOT NULL,
    project_id             UNIQUEIDENTIFIER NOT NULL,
    wave_id                UNIQUEIDENTIFIER NOT NULL,
    wave_version           INT              NOT NULL,
    configuration_hash     CHAR(64)         NOT NULL,
    selection_hash         CHAR(64)         NOT NULL,
    mapping_schema_version INT              NOT NULL,
    mapping_policy_version INT              NOT NULL,
    content_code_page      INT              NOT NULL,
    content_sha256         CHAR(64)         NOT NULL,
    size_bytes             BIGINT           NOT NULL,
    row_count              INT              NULL,
    outcome                TINYINT          NOT NULL,
    issue_count            INT              NOT NULL,
    issues_truncated       BIT              NOT NULL,
    display_file_name      NVARCHAR(260)    NULL,
    user_id                UNIQUEIDENTIFIER NOT NULL,
    uploaded_by            NVARCHAR(200)    NOT NULL,
    correlation_id         UNIQUEIDENTIFIER NOT NULL,
    idempotency_key        UNIQUEIDENTIFIER NOT NULL,
    created_at_utc         DATETIME2(3)     NOT NULL CONSTRAINT DF_mapping_validation_attempts_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_mapping_validation_attempts PRIMARY KEY (validation_id),
    -- Chave necessária para o FK composto dos filhos (issues) reforçar validation_id + tenant + projeto.
    CONSTRAINT UQ_mapping_validation_attempts_scope UNIQUE (validation_id, tenant_id, project_id),
    CONSTRAINT FK_mapping_validation_attempts_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.projects (tenant_id, project_id),
    -- FK composta para a VERSÃO IMUTÁVEL da onda: reforça fisicamente tenant/projeto (uma tentativa de um
    -- projeto NÃO pode apontar para a onda de outro projeto/tenant).
    CONSTRAINT FK_mapping_validation_attempts_wave_version FOREIGN KEY (wave_id, wave_version, tenant_id, project_id)
        REFERENCES dbo.wave_versions (wave_id, wave_version, tenant_id, project_id),
    CONSTRAINT CK_mapping_validation_attempts_size CHECK (size_bytes >= 0),
    CONSTRAINT CK_mapping_validation_attempts_rowcount CHECK (row_count IS NULL OR row_count >= 0),
    CONSTRAINT CK_mapping_validation_attempts_issuecount CHECK (issue_count >= 0),
    CONSTRAINT CK_mapping_validation_attempts_schemaver CHECK (mapping_schema_version >= 1),
    CONSTRAINT CK_mapping_validation_attempts_policyver CHECK (mapping_policy_version >= 1),
    CONSTRAINT CK_mapping_validation_attempts_codepage CHECK (content_code_page BETWEEN 1 AND 65535),
    CONSTRAINT CK_mapping_validation_attempts_outcome CHECK (outcome BETWEEN 0 AND 2)
);
GO

-- Idempotência: no store esta chave é OBRIGATÓRIA e NÃO NULA, logo um índice único normal (não filtrado)
-- é o backstop SQL contra corrida (duas requisições concorrentes com a mesma chave não criam duas linhas).
CREATE UNIQUE INDEX UX_mapping_validation_attempts_idempotency
    ON dbo.mapping_validation_attempts (tenant_id, project_id, idempotency_key);
GO

-- Índice de consulta futura pelo Portal (histórico por escopo, mais recente primeiro). Sem INCLUDE amplo.
CREATE INDEX IX_mapping_validation_attempts_scope_created
    ON dbo.mapping_validation_attempts (tenant_id, project_id, created_at_utc DESC, validation_id DESC);
GO

CREATE TABLE dbo.mapping_validation_issues
(
    validation_id  UNIQUEIDENTIFIER NOT NULL,
    tenant_id      UNIQUEIDENTIFIER NOT NULL,
    project_id     UNIQUEIDENTIFIER NOT NULL,
    issue_ordinal  INT              NOT NULL,
    line_number    INT              NULL,
    column_index   INT              NULL,
    issue_code     NVARCHAR(64)     NOT NULL,
    message        NVARCHAR(300)    NOT NULL,
    CONSTRAINT PK_mapping_validation_issues PRIMARY KEY (validation_id, issue_ordinal),
    -- FK composta ao attempt reforçando validation_id + tenant + projeto (nunca cruza escopo).
    CONSTRAINT FK_mapping_validation_issues_attempt FOREIGN KEY (validation_id, tenant_id, project_id)
        REFERENCES dbo.mapping_validation_attempts (validation_id, tenant_id, project_id),
    CONSTRAINT CK_mapping_validation_issues_ordinal CHECK (issue_ordinal >= 0),
    CONSTRAINT CK_mapping_validation_issues_line CHECK (line_number IS NULL OR line_number >= 1),
    CONSTRAINT CK_mapping_validation_issues_column CHECK (column_index IS NULL OR column_index >= 0)
);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE. Manutenção NÃO recebe grant algum.
GRANT SELECT, INSERT ON dbo.mapping_validation_attempts TO ab_app_role;
GRANT SELECT, INSERT ON dbo.mapping_validation_issues TO ab_app_role;
GO

-- Isolamento por tenant (RLS): as duas tabelas registram operações já autenticadas e carregam project_id
-- explicitamente; participam integralmente da política existente (FILTER + BLOCK AFTER INSERT).
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_validation_attempts,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_validation_attempts AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_validation_issues,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.mapping_validation_issues AFTER INSERT;
GO
