-- I5/EPIC-06 Passo 4 — AB-I5-012: evidência versionada e imutável do mapping CSV do Purview Network
-- Upload (runbook §25.8/§25.9). Distinta de dbo.mapping_csv_versions/dbo.mapping_csv_rows (Slice 2): esta
-- tabela NUNCA persiste o conteúdo das linhas (item 12 — só metadados/evidência: version, hash, row count,
-- created time, referência opaca de artefato) e o CHECK de dbo.mapping_csv_rows fixa Workload=Exchange /
-- IsArchive=TRUE / colunas SharePoint vazias, o que conflita estruturalmente com este Passo — aqui
-- IsArchive é RESOLVIDO por linha a partir do precheck canônico de mailbox (nunca fixo em TRUE), e o
-- conteúdo completo das linhas vive apenas no artefato imutável (mapping.csv), nunca em SQL. Reaproveita o
-- MESMO protocolo recuperável em duas transações curtas (reserva → publica fora do SQL → finaliza) e o
-- MESMO desenho de versionamento monotônico + índice único filtrado (no máximo uma utilizável por onda).
--
-- Aditiva, append-only (sob a ótica de conteúdo lógico — o único UPDATE permitido é a coluna status,
-- igual a 0005_slice2_grants) e não destrutiva: cria UMA tabela nova. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0030 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos.

CREATE TABLE dbo.purview_mapping_csv_versions
(
    wave_id              UNIQUEIDENTIFIER NOT NULL,
    mapping_version      INT              NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    evidence_fingerprint CHAR(64)         NOT NULL,
    content_sha256       CHAR(64)         NOT NULL,
    row_count            INT              NOT NULL,
    generated_by         NVARCHAR(200)    NOT NULL,
    created_at_utc       DATETIME2(3)     NOT NULL,
    status               TINYINT          NOT NULL, -- 0 Usable, 1 Superseded, 2 PendingArtifact
    artifact_path        NVARCHAR(400)    NOT NULL,
    artifact_size_bytes  BIGINT           NOT NULL,
    CONSTRAINT PK_purview_mapping_csv_versions PRIMARY KEY (wave_id, mapping_version),
    CONSTRAINT FK_pmcv_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    CONSTRAINT CK_pmcv_rowcount CHECK (row_count BETWEEN 1 AND 500),
    CONSTRAINT CK_pmcv_status CHECK (status BETWEEN 0 AND 2),
    CONSTRAINT CK_pmcv_artifact_size CHECK (artifact_size_bytes >= 0)
);
GO

-- No máximo UMA versão utilizável por onda (mesmo padrão de UX_mcv_single_usable).
CREATE UNIQUE INDEX UX_pmcv_single_usable ON dbo.purview_mapping_csv_versions (wave_id) WHERE status = 0;
GO

CREATE INDEX IX_pmcv_scope ON dbo.purview_mapping_csv_versions (tenant_id, project_id, wave_id, mapping_version);
GO

-- SELECT/INSERT (sem UPDATE/DELETE), exceto a coluna 'status' (marcar Superseded/promover Usable) —
-- hashes, fingerprint, row_count e artefato nunca podem ser alterados pela aplicação após a inserção.
GRANT SELECT, INSERT ON dbo.purview_mapping_csv_versions TO ab_app_role;
GO
GRANT UPDATE (status) ON dbo.purview_mapping_csv_versions TO ab_app_role;
GO

-- Isolamento por tenant (RLS): a tabela participa integralmente da política existente. Isolamento POR
-- PROJETO é reforçado pelo filtro explícito por project_id em toda query e pela PK/UNIQUE composta.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_mapping_csv_versions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_mapping_csv_versions AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_mapping_csv_versions AFTER UPDATE;
GO
