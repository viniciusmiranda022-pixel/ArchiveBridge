-- I6/EPIC-07 Passo 1 — AB-I6-001: custódia versionada/imutável do validation report / service result do
-- Purview e das suas linhas normalizadas por PST (itens 6/9-10). O conteúdo bruto é persistido como
-- evidência (hashado, revalidado a cada leitura) — bounded pelo mesmo limite do parser de Domain
-- (PurviewServiceResultReportParser.MaxReportBytes/MaxDataRows).
--
-- Aditiva, append-only e não destrutiva: cria DUAS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0033 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.

CREATE TABLE dbo.purview_service_result_report_versions
(
    wave_id              UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence     INT              NOT NULL,
    report_version       INT              NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    content_sha256       CHAR(64)         NOT NULL,
    rows_sha256          CHAR(64)         NOT NULL,
    raw_content          VARBINARY(MAX)   NOT NULL,
    raw_size_bytes       BIGINT           NOT NULL,
    row_count            INT              NOT NULL,
    declared_total_rows  INT              NULL,
    uploaded_by          NVARCHAR(200)    NOT NULL,
    created_at_utc       DATETIME2(3)     NOT NULL,
    evidence_hash        CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_service_result_report_versions PRIMARY KEY (wave_id, attempt_sequence, report_version),
    CONSTRAINT UQ_psrrv_scope UNIQUE (wave_id, attempt_sequence, report_version, tenant_id, project_id),
    -- Idempotência por conteúdo (item 10): o MESMO conteúdo bruto nunca produz duas versões distintas do
    -- MESMO plano.
    CONSTRAINT UQ_psrrv_content UNIQUE (wave_id, attempt_sequence, content_sha256),
    CONSTRAINT FK_psrrv_plan FOREIGN KEY (wave_id, attempt_sequence, tenant_id, project_id)
        REFERENCES dbo.purview_import_job_plans (wave_id, attempt_sequence, tenant_id, project_id),
    CONSTRAINT CK_psrrv_attempt_sequence CHECK (attempt_sequence >= 1),
    CONSTRAINT CK_psrrv_report_version CHECK (report_version >= 1),
    CONSTRAINT CK_psrrv_rowcount CHECK (row_count BETWEEN 1 AND 2000),
    CONSTRAINT CK_psrrv_declared_total_rows CHECK (declared_total_rows IS NULL OR declared_total_rows >= 1),
    CONSTRAINT CK_psrrv_size CHECK (raw_size_bytes BETWEEN 1 AND 2000000)
);
GO

CREATE INDEX IX_psrrv_scope ON dbo.purview_service_result_report_versions (tenant_id, project_id, wave_id, attempt_sequence, report_version);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma versão é jamais atualizada ou apagada.
GRANT SELECT, INSERT ON dbo.purview_service_result_report_versions TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_service_result_report_versions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_service_result_report_versions AFTER INSERT;
GO

-- Linha normalizada por PST (item 7): contadores NULL representam Unknown/NotReported — NUNCA zero. A
-- identidade é o nome remoto exato (mesma chave de correlação 1:1 usada pela Application), não um
-- ordinal de leitura.
CREATE TABLE dbo.purview_service_result_rows
(
    wave_id              UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence     INT              NOT NULL,
    report_version       INT              NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    remote_pst_name      NVARCHAR(300)    NOT NULL,
    status               TINYINT          NOT NULL, -- PurviewServiceResultRowStatus (0..3)
    imported_item_count  BIGINT           NULL,
    imported_size_bytes  BIGINT           NULL,
    skipped_item_count   BIGINT           NULL,
    corrupted_item_count BIGINT           NULL,
    CONSTRAINT PK_purview_service_result_rows PRIMARY KEY (wave_id, attempt_sequence, report_version, remote_pst_name),
    CONSTRAINT FK_psrr_version FOREIGN KEY (wave_id, attempt_sequence, report_version, tenant_id, project_id)
        REFERENCES dbo.purview_service_result_report_versions (wave_id, attempt_sequence, report_version, tenant_id, project_id),
    CONSTRAINT CK_psrr_status CHECK (status BETWEEN 0 AND 3),
    CONSTRAINT CK_psrr_imported_item_count CHECK (imported_item_count IS NULL OR imported_item_count >= 0),
    CONSTRAINT CK_psrr_imported_size_bytes CHECK (imported_size_bytes IS NULL OR imported_size_bytes >= 0),
    CONSTRAINT CK_psrr_skipped_item_count CHECK (skipped_item_count IS NULL OR skipped_item_count >= 0),
    CONSTRAINT CK_psrr_corrupted_item_count CHECK (corrupted_item_count IS NULL OR corrupted_item_count >= 0)
);
GO

CREATE INDEX IX_psrr_scope ON dbo.purview_service_result_rows (tenant_id, project_id, wave_id, attempt_sequence, report_version);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma linha é jamais atualizada ou apagada.
GRANT SELECT, INSERT ON dbo.purview_service_result_rows TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_service_result_rows,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_service_result_rows AFTER INSERT;
GO
