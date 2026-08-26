-- I6/EPIC-07 Passo 3 — AB-I6-007: fundação de reconciliação expected-vs-observed (runbook §26). Transforma
-- evidências canônicas JÁ PERSISTIDAS (mapping/binding/execução/upload revalidados sem drift do Passo 4,
-- service result do Purview do Passo 1, snapshots EXO before/after do Passo 2) em um read model técnico,
-- determinístico e auditável por PST/archive/wave — NUNCA um certificate, disposition humana/final,
-- ReconciliationOutcome=PASS terminal ou conclusão de wave/projeto (STOP-THE-LINE do work order).
--   disposition (ReconciliationDisposition): MatchedWithinEvidence=0, Mismatch=1, IncompleteEvidence=2,
--     BlockedIntegrity=3, ExtraInProvider=4
--   observed_status (PurviewServiceResultRowStatus): Unknown=0, Succeeded=1, Failed=2, SkippedOrCorrupted=3
--
-- Aditiva, append-only e não destrutiva: cria TRÊS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0035 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.

-- Header IMUTÁVEL/versionado por (onda, plano de import job) — item 10/11. source_fingerprint é a chave de
-- convergência idempotente (mesma evidência-fonte ⇒ mesma versão); assessment_hash cobre TODOS os campos
-- do header persistidos (tamper-evident, item 11). attempt_sequence identifica o plano (mesmo padrão de
-- dbo.purview_service_result_report_versions) — o nome planejado (planned_job_name) já é único por
-- dbo.purview_import_job_plans.
CREATE TABLE dbo.purview_reconciliation_assessments
(
    wave_id                UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence       INT              NOT NULL,
    assessment_version     INT              NOT NULL,
    tenant_id              UNIQUEIDENTIFIER NOT NULL,
    project_id             UNIQUEIDENTIFIER NOT NULL,
    source_fingerprint     CHAR(64)         NOT NULL,
    pst_item_count         INT              NOT NULL,
    pst_items_sha256       CHAR(64)         NOT NULL,
    archive_item_count     INT              NOT NULL,
    archive_items_sha256   CHAR(64)         NOT NULL,
    correlation_id         UNIQUEIDENTIFIER NOT NULL,
    created_at_utc         DATETIME2(3)     NOT NULL,
    assessment_hash        CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_reconciliation_assessments PRIMARY KEY (wave_id, attempt_sequence, assessment_version),
    CONSTRAINT UQ_pra_scope UNIQUE (wave_id, attempt_sequence, assessment_version, tenant_id, project_id),
    -- Idempotência por evidência-fonte (item 10): a MESMA evidência-fonte nunca produz duas versões
    -- distintas do MESMO plano.
    CONSTRAINT UQ_pra_fingerprint UNIQUE (wave_id, attempt_sequence, source_fingerprint),
    CONSTRAINT FK_pra_plan FOREIGN KEY (wave_id, attempt_sequence, tenant_id, project_id)
        REFERENCES dbo.purview_import_job_plans (wave_id, attempt_sequence, tenant_id, project_id),
    CONSTRAINT CK_pra_version CHECK (assessment_version >= 1),
    CONSTRAINT CK_pra_pst_item_count CHECK (pst_item_count BETWEEN 0 AND 2000),
    CONSTRAINT CK_pra_archive_item_count CHECK (archive_item_count BETWEEN 0 AND 2000)
);
GO

CREATE INDEX IX_pra_latest ON dbo.purview_reconciliation_assessments (tenant_id, project_id, wave_id, attempt_sequence, assessment_version DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma avaliação é jamais atualizada ou apagada.
GRANT SELECT, INSERT ON dbo.purview_reconciliation_assessments TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_assessments,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_assessments AFTER INSERT;
GO

-- Item de PST filho (item 5-7): identidade estável por remote_pst_name dentro da avaliação pai — um PST
-- esperado ausente do provider, um item extra no provider, e um item correlacionado (matched/mismatch/
-- inconclusivo) convivem na MESMA tabela, cada um com disposition explícita. Contadores/observed_status
-- NULL representam Unknown/NotReported — NUNCA zero (item 5).
CREATE TABLE dbo.purview_reconciliation_pst_items
(
    wave_id               UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence      INT              NOT NULL,
    assessment_version    INT              NOT NULL,
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    remote_pst_name       NVARCHAR(300)    NOT NULL,
    disposition           TINYINT          NOT NULL,
    observed_status       TINYINT          NULL,
    imported_item_count   BIGINT           NULL,
    imported_size_bytes   BIGINT           NULL,
    skipped_item_count    BIGINT           NULL,
    corrupted_item_count  BIGINT           NULL,
    CONSTRAINT PK_purview_reconciliation_pst_items PRIMARY KEY (wave_id, attempt_sequence, assessment_version, remote_pst_name),
    CONSTRAINT FK_prpi_assessment FOREIGN KEY (wave_id, attempt_sequence, assessment_version, tenant_id, project_id)
        REFERENCES dbo.purview_reconciliation_assessments (wave_id, attempt_sequence, assessment_version, tenant_id, project_id),
    CONSTRAINT CK_prpi_disposition CHECK (disposition BETWEEN 0 AND 4),
    CONSTRAINT CK_prpi_observed_status CHECK (observed_status IS NULL OR observed_status BETWEEN 0 AND 3),
    CONSTRAINT CK_prpi_imported_item_count CHECK (imported_item_count IS NULL OR imported_item_count >= 0),
    CONSTRAINT CK_prpi_imported_size_bytes CHECK (imported_size_bytes IS NULL OR imported_size_bytes >= 0),
    CONSTRAINT CK_prpi_skipped_item_count CHECK (skipped_item_count IS NULL OR skipped_item_count >= 0),
    CONSTRAINT CK_prpi_corrupted_item_count CHECK (corrupted_item_count IS NULL OR corrupted_item_count >= 0)
);
GO

CREATE INDEX IX_prpi_scope ON dbo.purview_reconciliation_pst_items (tenant_id, project_id, wave_id, attempt_sequence, assessment_version);
GO

-- Append-only: apenas SELECT/INSERT — nenhum item é jamais atualizado ou apagado.
GRANT SELECT, INSERT ON dbo.purview_reconciliation_pst_items TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_pst_items,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_pst_items AFTER INSERT;
GO

-- Item de archive filho (item 8-9): identidade estável por archive_identity dentro da avaliação pai —
-- before_captured/after_captured registram explicitamente quais lados existiam no instante do cálculo;
-- deltas são NULL (Unknown) sempre que qualquer lado da métrica for desconhecido/ausente (item 9), nunca
-- fabricados.
CREATE TABLE dbo.purview_reconciliation_archive_items
(
    wave_id                       UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence              INT              NOT NULL,
    assessment_version            INT              NOT NULL,
    tenant_id                     UNIQUEIDENTIFIER NOT NULL,
    project_id                    UNIQUEIDENTIFIER NOT NULL,
    archive_identity              NVARCHAR(320)    NOT NULL,
    disposition                   TINYINT          NOT NULL,
    before_captured               BIT              NOT NULL,
    after_captured                BIT              NOT NULL,
    item_count_delta              BIGINT           NULL,
    total_item_size_bytes_delta   BIGINT           NULL,
    CONSTRAINT PK_purview_reconciliation_archive_items PRIMARY KEY (wave_id, attempt_sequence, assessment_version, archive_identity),
    CONSTRAINT FK_prai_assessment FOREIGN KEY (wave_id, attempt_sequence, assessment_version, tenant_id, project_id)
        REFERENCES dbo.purview_reconciliation_assessments (wave_id, attempt_sequence, assessment_version, tenant_id, project_id),
    CONSTRAINT CK_prai_disposition CHECK (disposition BETWEEN 0 AND 4)
);
GO

CREATE INDEX IX_prai_scope ON dbo.purview_reconciliation_archive_items (tenant_id, project_id, wave_id, attempt_sequence, assessment_version);
GO

-- Append-only: apenas SELECT/INSERT — nenhum item é jamais atualizado ou apagado.
GRANT SELECT, INSERT ON dbo.purview_reconciliation_archive_items TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_archive_items,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_archive_items AFTER INSERT;
GO
