-- I5/EPIC-06 Passo 1 — Purview Capability Registry & Mailbox/Tenant Prechecks (AB-I5-001). Migração
-- ADITIVA e protegida por hash: só cria objetos novos, sem alterar migrations já aplicadas e sem operação
-- destrutiva. Persiste APENAS metadados de governança/precheck read-only (versão, status, hashes,
-- identidade de archive/mailbox, holds, estatísticas estruturadas em bytes, timestamps): nada de SAS,
-- credencial, token, transcript PowerShell bruto ou conteúdo de mailbox (assunto/corpo/remetente/
-- destinatário/anexo). Nenhuma tabela desta migração representa ou registra mutação de tenant/mailbox —
-- ambas são somente-leitura por desenho (work order AB-I5-001 item 5).
--   provider (TargetProvider): Purview=0
--   status (CapabilityStatus): Unknown=0, Unsupported=1, Contractual=2, Preview=3, GeneralAvailability=4
--   archive_status (MailboxArchiveStatus): Unknown=0, None=1, Disabled=2, Active=3

-- Capability evidence por rota, versionada e escopada a tenant/projeto/provedor (item 3). Append-only: a
-- vigente é sempre a linha de maior version para o mesmo (tenant, projeto, provider, route_key). O índice
-- único é o backstop de concorrência — duas descobertas concorrentes calculando a MESMA próxima versão
-- nunca duplicam nem corrompem a linha vencedora (mesmo padrão de ev_connector_capability_handshakes/
-- ev_connector_inventory_snapshots).
CREATE TABLE dbo.purview_capability_evidence
(
    evidence_id               UNIQUEIDENTIFIER NOT NULL,
    tenant_id                 UNIQUEIDENTIFIER NOT NULL,
    project_id                UNIQUEIDENTIFIER NOT NULL,
    provider                  TINYINT          NOT NULL,
    route_key                 NVARCHAR(200)    NOT NULL,
    version                   INT              NOT NULL,
    status                    TINYINT          NOT NULL,
    source_reference          NVARCHAR(400)    NULL,
    documentation_version     NVARCHAR(100)    NULL,
    capability_version_label  NVARCHAR(100)    NULL,
    observed_at_utc           DATETIME2(3)     NOT NULL,
    correlation_id            UNIQUEIDENTIFIER NOT NULL,
    recorded_at_utc           DATETIME2(3)     NOT NULL,
    evidence_hash             CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_capability_evidence PRIMARY KEY (evidence_id),
    CONSTRAINT UQ_pce_scope_version UNIQUE (tenant_id, project_id, provider, route_key, version),
    CONSTRAINT CK_pce_provider CHECK (provider = 0),
    CONSTRAINT CK_pce_status CHECK (status BETWEEN 0 AND 4),
    CONSTRAINT CK_pce_version CHECK (version >= 1)
);

CREATE INDEX IX_pce_latest ON dbo.purview_capability_evidence (tenant_id, project_id, provider, route_key, version DESC);
GO

-- Snapshots de precheck read-only de tenant/mailbox por archive de destino, versionados e escopados a
-- tenant/projeto (item 4/11). Append-only: a vigente é sempre a linha de maior version para o mesmo
-- (tenant, projeto, archive_identity). archive_identity é o TargetArchiveId canônico (upper-invariant) —
-- a mesma chave de agrupamento já usada pelo capacity gate por onda (Slice 2, CapacityPlanner).
CREATE TABLE dbo.purview_mailbox_prechecks
(
    snapshot_id                      UNIQUEIDENTIFIER NOT NULL,
    tenant_id                        UNIQUEIDENTIFIER NOT NULL,
    project_id                       UNIQUEIDENTIFIER NOT NULL,
    archive_identity                 NVARCHAR(320)    NOT NULL,
    mailbox_display                  NVARCHAR(320)    NOT NULL,
    version                          INT              NOT NULL,
    exchange_guid                    UNIQUEIDENTIFIER NULL,
    archive_guid                     UNIQUEIDENTIFIER NULL,
    archive_status                   TINYINT          NOT NULL,
    recipient_type_details           NVARCHAR(100)    NULL,
    auto_expanding_archive_enabled   BIT              NOT NULL,
    litigation_hold_enabled          BIT              NOT NULL,
    retention_hold_enabled           BIT              NOT NULL,
    archive_item_count               BIGINT           NULL,
    archive_total_size_bytes         BIGINT           NULL,
    observed_available_bytes         BIGINT           NULL,
    observed_at_utc                  DATETIME2(3)     NOT NULL,
    correlation_id                   UNIQUEIDENTIFIER NOT NULL,
    recorded_at_utc                  DATETIME2(3)     NOT NULL,
    snapshot_hash                    CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_mailbox_prechecks PRIMARY KEY (snapshot_id),
    CONSTRAINT UQ_pmp_scope_version UNIQUE (tenant_id, project_id, archive_identity, version),
    CONSTRAINT CK_pmp_archive_status CHECK (archive_status BETWEEN 0 AND 3),
    CONSTRAINT CK_pmp_version CHECK (version >= 1),
    CONSTRAINT CK_pmp_item_count CHECK (archive_item_count IS NULL OR archive_item_count >= 0),
    CONSTRAINT CK_pmp_total_size CHECK (archive_total_size_bytes IS NULL OR archive_total_size_bytes >= 0),
    CONSTRAINT CK_pmp_available_bytes CHECK (observed_available_bytes IS NULL OR observed_available_bytes >= 0)
);

CREATE INDEX IX_pmp_latest ON dbo.purview_mailbox_prechecks (tenant_id, project_id, archive_identity, version DESC);
GO

-- Privilégios: a aplicação só grava (append) e lê; a manutenção só lê (auditoria). Nenhum UPDATE/DELETE é
-- concedido a nenhuma identidade — as duas tabelas são estritamente append-only.
GRANT SELECT, INSERT ON dbo.purview_capability_evidence TO ab_app_role;
GRANT SELECT ON dbo.purview_capability_evidence TO ab_maintenance_role;
GRANT SELECT, INSERT ON dbo.purview_mailbox_prechecks TO ab_app_role;
GRANT SELECT ON dbo.purview_mailbox_prechecks TO ab_maintenance_role;
GO

-- Isolamento por tenant (defesa em profundidade) nas novas tabelas. O isolamento POR PROJETO é reforçado
-- pelo filtro explícito por project_id em toda query (Contracts/Infrastructure) e pela UNIQUE composta.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_capability_evidence,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_capability_evidence AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_mailbox_prechecks,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_mailbox_prechecks AFTER INSERT;
GO
