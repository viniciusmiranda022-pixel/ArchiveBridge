-- I6/EPIC-07 Passo 2 — AB-I6-005: custódia versionada/imutável das observações de estatísticas do
-- archive EXO before/after (runbook §25.2/§26.2) e das estatísticas de pasta filhas por snapshot. Captura
-- estritamente READ-ONLY: nenhuma tabela desta migração representa ou registra mutação de mailbox/tenant/
-- hold (item 3). Ausência de campo do provider é persistida como NULL (Unknown/NotReported) — NUNCA
-- zero/false/data mínima (item 7).
--   phase (ExoStatisticsPhase): BeforeImport=0, AfterImport=1
--   archive_status (MailboxArchiveStatus): Unknown=0, None=1, Disabled=2, Active=3
--
-- Aditiva, append-only e não destrutiva: cria DUAS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0034 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.

-- Header IMUTÁVEL/versionado por (onda, archive, fase) — item 1/11. archive_identity é o TargetArchiveId
-- canônico (upper-invariant) resolvido server-side a partir da seleção da onda, nunca fornecido como
-- autoridade pelo caller. observation_hash é a chave de convergência idempotente (item 12: mesma
-- observação lógica ⇒ mesma versão); snapshot_hash cobre TODOS os campos persistidos (tamper-evident,
-- item 11).
CREATE TABLE dbo.purview_exo_archive_statistics_snapshots
(
    wave_id                          UNIQUEIDENTIFIER NOT NULL,
    archive_identity                 NVARCHAR(320)    NOT NULL,
    phase                            TINYINT          NOT NULL,
    snapshot_version                 INT              NOT NULL,
    tenant_id                        UNIQUEIDENTIFIER NOT NULL,
    project_id                       UNIQUEIDENTIFIER NOT NULL,
    archive_status                   TINYINT          NOT NULL,
    exchange_guid                    UNIQUEIDENTIFIER NULL,
    archive_guid                     UNIQUEIDENTIFIER NULL,
    item_count                       BIGINT           NULL,
    total_item_size_bytes            BIGINT           NULL,
    total_deleted_item_size_bytes    BIGINT           NULL,
    last_logon_time_utc              DATETIME2(3)     NULL,
    retention_hold_enabled           BIT              NULL,
    litigation_hold_enabled          BIT              NULL,
    auto_expanding_archive_enabled   BIT              NULL,
    folder_count                     INT              NOT NULL,
    folders_sha256                   CHAR(64)         NOT NULL,
    observation_hash                 CHAR(64)         NOT NULL,
    observed_at_utc                  DATETIME2(3)     NOT NULL,
    correlation_id                   UNIQUEIDENTIFIER NOT NULL,
    created_at_utc                   DATETIME2(3)     NOT NULL,
    snapshot_hash                    CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_exo_archive_statistics_snapshots PRIMARY KEY (wave_id, archive_identity, phase, snapshot_version),
    CONSTRAINT UQ_peass_scope UNIQUE (wave_id, archive_identity, phase, snapshot_version, tenant_id, project_id),
    -- Idempotência por conteúdo lógico (item 12): a MESMA observação nunca produz duas versões distintas
    -- do MESMO escopo/fase.
    CONSTRAINT UQ_peass_observation UNIQUE (wave_id, archive_identity, phase, observation_hash),
    CONSTRAINT FK_peass_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    CONSTRAINT CK_peass_phase CHECK (phase IN (0, 1)),
    CONSTRAINT CK_peass_version CHECK (snapshot_version >= 1),
    CONSTRAINT CK_peass_archive_status CHECK (archive_status BETWEEN 0 AND 3),
    CONSTRAINT CK_peass_item_count CHECK (item_count IS NULL OR item_count >= 0),
    CONSTRAINT CK_peass_total_size CHECK (total_item_size_bytes IS NULL OR total_item_size_bytes >= 0),
    CONSTRAINT CK_peass_deleted_size CHECK (total_deleted_item_size_bytes IS NULL OR total_deleted_item_size_bytes >= 0),
    CONSTRAINT CK_peass_folder_count CHECK (folder_count BETWEEN 0 AND 2000)
);
GO

CREATE INDEX IX_peass_latest ON dbo.purview_exo_archive_statistics_snapshots (tenant_id, project_id, wave_id, archive_identity, phase, snapshot_version DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhum snapshot é jamais atualizado ou apagado.
GRANT SELECT, INSERT ON dbo.purview_exo_archive_statistics_snapshots TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_exo_archive_statistics_snapshots,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_exo_archive_statistics_snapshots AFTER INSERT;
GO

-- Estatística de pasta filha (item 6/9): identidade estável por folder_path dentro do (wave, archive,
-- phase, snapshot_version) pai — bounded a 2000 pastas/snapshot (mesmo limite de CK_peass_folder_count),
-- deduplicada/canonicalizada pelo Domain antes da persistência (ExoArchiveFolderStatisticsSet.Canonicalize).
-- Contadores/datas NULL representam Unknown/NotReported — NUNCA zero/data mínima (item 7).
CREATE TABLE dbo.purview_exo_archive_folder_statistics
(
    wave_id                           UNIQUEIDENTIFIER NOT NULL,
    archive_identity                  NVARCHAR(320)    NOT NULL,
    phase                             TINYINT          NOT NULL,
    snapshot_version                  INT              NOT NULL,
    tenant_id                         UNIQUEIDENTIFIER NOT NULL,
    project_id                        UNIQUEIDENTIFIER NOT NULL,
    folder_path                       NVARCHAR(400)    NOT NULL,
    folder_type                       NVARCHAR(100)    NOT NULL,
    items_in_folder                   BIGINT           NULL,
    items_in_folder_and_subfolders    BIGINT           NULL,
    folder_size_bytes                 BIGINT           NULL,
    folder_and_subfolder_size_bytes   BIGINT           NULL,
    oldest_item_received_date_utc     DATETIME2(3)     NULL,
    newest_item_received_date_utc     DATETIME2(3)     NULL,
    CONSTRAINT PK_purview_exo_archive_folder_statistics PRIMARY KEY (wave_id, archive_identity, phase, snapshot_version, folder_path),
    CONSTRAINT FK_peafs_snapshot FOREIGN KEY (wave_id, archive_identity, phase, snapshot_version, tenant_id, project_id)
        REFERENCES dbo.purview_exo_archive_statistics_snapshots (wave_id, archive_identity, phase, snapshot_version, tenant_id, project_id),
    CONSTRAINT CK_peafs_items_in_folder CHECK (items_in_folder IS NULL OR items_in_folder >= 0),
    CONSTRAINT CK_peafs_items_in_folder_and_sub CHECK (items_in_folder_and_subfolders IS NULL OR items_in_folder_and_subfolders >= 0),
    CONSTRAINT CK_peafs_folder_size CHECK (folder_size_bytes IS NULL OR folder_size_bytes >= 0),
    CONSTRAINT CK_peafs_folder_and_sub_size CHECK (folder_and_subfolder_size_bytes IS NULL OR folder_and_subfolder_size_bytes >= 0),
    -- Defesa em profundidade da mesma regra do Domain (ExoArchiveFolderStatistic): data temporalmente
    -- impossível nunca é persistida como canônica.
    CONSTRAINT CK_peafs_date_order CHECK (
        oldest_item_received_date_utc IS NULL OR newest_item_received_date_utc IS NULL
        OR oldest_item_received_date_utc <= newest_item_received_date_utc)
);
GO

CREATE INDEX IX_peafs_scope ON dbo.purview_exo_archive_folder_statistics (tenant_id, project_id, wave_id, archive_identity, phase, snapshot_version);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma linha é jamais atualizada ou apagada.
GRANT SELECT, INSERT ON dbo.purview_exo_archive_folder_statistics TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_exo_archive_folder_statistics,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_exo_archive_folder_statistics AFTER INSERT;
GO
