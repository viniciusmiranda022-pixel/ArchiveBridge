-- Slice 4C (Passo 1) — Enterprise Vault Connector Enrollment & Inventory Foundation (AB-4C-001).
--
-- Aditiva e não destrutiva: cria CINCO tabelas novas. Nenhum DROP, nenhum UPDATE de dados, nenhuma
-- redefinição de 0001-0022 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.
--
-- Persiste APENAS metadados operacionais: identidade opaca do connector, thumbprint de chave PÚBLICA
-- (nunca privada), hash SHA-256 do segredo do enrollment token (nunca o segredo em si), hostname/site
-- sanitizados, resultado do capability handshake e inventário normalizado (identidade externa opaca do
-- archive, tipo, vault store, status, diagnósticos curtos). NUNCA conteúdo de mailbox (assunto, corpo,
-- anexo), credencial EV, transcript bruto ou caminho físico.

-- Enrollment tokens (dbo.ev_connector_enrollment_tokens): uso único, validade máxima de 15 minutos
-- (reforçada aqui E no Domain — defesa em profundidade, mesmo padrão de CK_pst_partition_executions_byte_identical).
-- token_hash é a ÚNICA credencial de resgate — UNIQUE global (não escopado) porque o resgate deve
-- localizar o token SEM conhecer previamente o tenant/projeto (o token é quem determina o escopo).
CREATE TABLE dbo.ev_connector_enrollment_tokens
(
    token_id                 UNIQUEIDENTIFIER NOT NULL,
    tenant_id                UNIQUEIDENTIFIER NOT NULL,
    project_id                UNIQUEIDENTIFIER NOT NULL,
    token_hash                 CHAR(64)         NOT NULL,
    requested_by                NVARCHAR(200)    NOT NULL,
    correlation_id                UNIQUEIDENTIFIER NOT NULL,
    issued_at_utc                  DATETIME2(3)     NOT NULL,
    expires_at_utc                   DATETIME2(3)     NOT NULL,
    status                             TINYINT          NOT NULL, -- 0 Issued, 1 Consumed, 2 Revoked
    consumed_at_utc                     DATETIME2(3)     NULL,
    consumed_by_connector_id              UNIQUEIDENTIFIER NULL,
    CONSTRAINT PK_ev_connector_enrollment_tokens PRIMARY KEY (token_id),
    CONSTRAINT UQ_ev_connector_enrollment_tokens_hash UNIQUE (token_hash),
    CONSTRAINT CK_ev_connector_enrollment_tokens_status CHECK (status BETWEEN 0 AND 2),
    -- Janela nunca excede 15 minutos (900s) — mesma regra de EnrollmentToken.MaxTtl no Domain.
    CONSTRAINT CK_ev_connector_enrollment_tokens_ttl CHECK (
        expires_at_utc > issued_at_utc AND DATEDIFF(SECOND, issued_at_utc, expires_at_utc) <= 900),
    -- consumed_at_utc/consumed_by_connector_id só existem quando status = Consumed (1) — nunca divergem.
    CONSTRAINT CK_ev_connector_enrollment_tokens_consumed_fields CHECK (
        (status = 1 AND consumed_at_utc IS NOT NULL AND consumed_by_connector_id IS NOT NULL)
        OR (status <> 1 AND consumed_at_utc IS NULL AND consumed_by_connector_id IS NULL))
);
GO

-- Emissão/leitura pela aplicação; resgate atualiza status/consumed_* (UPDATE condicional sob a identidade
-- do tenant já resolvido); revogação atualiza status. Nenhum DELETE — histórico de tokens é evidência
-- (inclusive os expirados/revogados/consumidos).
GRANT SELECT, INSERT ON dbo.ev_connector_enrollment_tokens TO ab_app_role;
GO
GRANT UPDATE (status, consumed_at_utc, consumed_by_connector_id) ON dbo.ev_connector_enrollment_tokens TO ab_app_role;
GO

-- Manutenção recebe SOMENTE SELECT (nunca escreve efeito de negócio): localiza o token pelo hash do
-- segredo apresentado ANTES de o tenant/projeto serem conhecidos — é o PRÓPRIO token quem os determina —
-- mesma restrição estritamente-leitura de SqlEvDiscoveryPendingScopeReader (Slice 3).
GRANT SELECT ON dbo.ev_connector_enrollment_tokens TO ab_maintenance_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_enrollment_tokens,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_enrollment_tokens AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_enrollment_tokens AFTER UPDATE;
GO

-- Identidade durável de connectors (dbo.ev_connectors): IDEMPOTENTE por (tenant, projeto, thumbprint) —
-- reinstalar o mesmo connector converge nesta linha (UPDATE dos campos operacionais), nunca cria uma
-- segunda identidade. UQ_ev_connectors_scope existe para o FK composto dos filhos (mesmo padrão de
-- UQ_pst_partition_plan_parts_scope em 0022).
CREATE TABLE dbo.ev_connectors
(
    connector_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id                UNIQUEIDENTIFIER NOT NULL,
    project_id                 UNIQUEIDENTIFIER NOT NULL,
    thumbprint                  CHAR(64)         NOT NULL,
    hostname                     NVARCHAR(260)    NOT NULL,
    site_name                      NVARCHAR(200)    NOT NULL,
    connector_version                NVARCHAR(50)     NOT NULL,
    status                             TINYINT          NOT NULL, -- 0 Active, 1 Revoked
    enrolled_via_token_id                UNIQUEIDENTIFIER NOT NULL,
    registered_at_utc                      DATETIME2(3)     NOT NULL,
    updated_at_utc                           DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_connectors PRIMARY KEY (connector_id),
    CONSTRAINT UQ_ev_connectors_scope UNIQUE (connector_id, tenant_id, project_id),
    CONSTRAINT UQ_ev_connectors_thumbprint UNIQUE (tenant_id, project_id, thumbprint),
    CONSTRAINT CK_ev_connectors_status CHECK (status BETWEEN 0 AND 1),
    CONSTRAINT CK_ev_connectors_timestamps CHECK (updated_at_utc >= registered_at_utc)
);
GO

GRANT SELECT, INSERT ON dbo.ev_connectors TO ab_app_role;
GO
GRANT UPDATE (hostname, site_name, connector_version, status, enrolled_via_token_id, updated_at_utc)
    ON dbo.ev_connectors TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connectors,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connectors AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connectors AFTER UPDATE;
GO

-- Handshakes de capability/versão (dbo.ev_connector_capability_handshakes): append-only, evidência
-- histórica completa. export_capable é sempre reforçado no BANCO como derivado de support_level +
-- powershell_snapin_available — a MESMA regra do Domain (ConnectorCapabilityHandshake.Evaluate), defesa
-- em profundidade (mesmo padrão de CK_pst_partition_executions_byte_identical em 0022).
CREATE TABLE dbo.ev_connector_capability_handshakes
(
    handshake_id                UNIQUEIDENTIFIER NOT NULL,
    connector_id                 UNIQUEIDENTIFIER NOT NULL,
    tenant_id                     UNIQUEIDENTIFIER NOT NULL,
    project_id                     UNIQUEIDENTIFIER NOT NULL,
    ev_version_display               NVARCHAR(100)    NOT NULL,
    powershell_snapin_available        BIT              NOT NULL,
    support_level                        TINYINT          NOT NULL, -- 0 Unknown,1 NotSupported,2 Compatible,3 Tested,4 Certified
    export_capable                         BIT              NOT NULL,
    blocking_reason                          NVARCHAR(200)    NULL,
    correlation_id                             UNIQUEIDENTIFIER NOT NULL,
    collected_at_utc                             DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_connector_capability_handshakes PRIMARY KEY (handshake_id),
    CONSTRAINT FK_ev_cch_connector FOREIGN KEY (connector_id, tenant_id, project_id)
        REFERENCES dbo.ev_connectors (connector_id, tenant_id, project_id),
    CONSTRAINT CK_ev_cch_support_level CHECK (support_level BETWEEN 0 AND 4),
    CONSTRAINT CK_ev_cch_export_capable CHECK (
        export_capable = CASE
            WHEN support_level = 4 AND powershell_snapin_available = 1 THEN CONVERT(BIT, 1)
            ELSE CONVERT(BIT, 0)
        END),
    CONSTRAINT CK_ev_cch_blocking_reason CHECK (
        (export_capable = 1 AND blocking_reason IS NULL) OR (export_capable = 0 AND blocking_reason IS NOT NULL))
);
GO

-- Índice de consulta do handshake vigente (mais recente) por connector.
CREATE INDEX IX_ev_cch_connector_latest ON dbo.ev_connector_capability_handshakes (connector_id, collected_at_utc DESC);
GO

-- Append-only: apenas SELECT/INSERT. Manutenção NÃO recebe grant algum.
GRANT SELECT, INSERT ON dbo.ev_connector_capability_handshakes TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_capability_handshakes,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_capability_handshakes AFTER INSERT;
GO

-- Snapshots de inventário (dbo.ev_connector_inventory_snapshots): append-only e versionado. UNIQUE
-- (connector_id, version) é o backstop de concorrência — duas submissões concorrentes calculando a mesma
-- próxima versão convergem para uma única linha (nunca duas).
CREATE TABLE dbo.ev_connector_inventory_snapshots
(
    snapshot_id              UNIQUEIDENTIFIER NOT NULL,
    connector_id               UNIQUEIDENTIFIER NOT NULL,
    tenant_id                    UNIQUEIDENTIFIER NOT NULL,
    project_id                     UNIQUEIDENTIFIER NOT NULL,
    version                          INT              NOT NULL,
    snapshot_hash                      CHAR(64)         NOT NULL,
    archive_count                        INT              NOT NULL,
    correlation_id                         UNIQUEIDENTIFIER NOT NULL,
    collected_at_utc                         DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_connector_inventory_snapshots PRIMARY KEY (snapshot_id),
    CONSTRAINT UQ_ev_cis_scope UNIQUE (snapshot_id, tenant_id, project_id),
    CONSTRAINT FK_ev_cis_connector FOREIGN KEY (connector_id, tenant_id, project_id)
        REFERENCES dbo.ev_connectors (connector_id, tenant_id, project_id),
    CONSTRAINT CK_ev_cis_version_positive CHECK (version > 0),
    CONSTRAINT CK_ev_cis_archive_count CHECK (archive_count >= 0),
    CONSTRAINT UX_ev_cis_connector_version UNIQUE (connector_id, version)
);
GO

CREATE INDEX IX_ev_cis_connector_latest ON dbo.ev_connector_inventory_snapshots (connector_id, version DESC);
GO

GRANT SELECT, INSERT ON dbo.ev_connector_inventory_snapshots TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_inventory_snapshots,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_inventory_snapshots AFTER INSERT;
GO

-- Archives normalizados de UM snapshot (dbo.ev_connector_inventory_archives): filha append-only de
-- ev_connector_inventory_snapshots. tenant_id/project_id são duplicados aqui (mesmo padrão de
-- pst_partition_plan_parts em 0021/0022) para permitir o FK composto e a própria RLS na tabela filha —
-- NUNCA conteúdo de mailbox, apenas identidade externa opaca, tipo, vault store e diagnósticos curtos.
CREATE TABLE dbo.ev_connector_inventory_archives
(
    snapshot_id             UNIQUEIDENTIFIER NOT NULL,
    tenant_id                 UNIQUEIDENTIFIER NOT NULL,
    project_id                  UNIQUEIDENTIFIER NOT NULL,
    external_archive_id           NVARCHAR(300)    NOT NULL,
    archive_type                    NVARCHAR(50)     NOT NULL,
    vault_store_name                  NVARCHAR(200)    NULL,
    status                               TINYINT          NOT NULL, -- 0 Unknown,1 Active,2 Inactive
    capability_diagnostics                NVARCHAR(1000)   NOT NULL,
    CONSTRAINT PK_ev_connector_inventory_archives PRIMARY KEY (snapshot_id, external_archive_id),
    CONSTRAINT FK_ev_cia_snapshot FOREIGN KEY (snapshot_id, tenant_id, project_id)
        REFERENCES dbo.ev_connector_inventory_snapshots (snapshot_id, tenant_id, project_id),
    CONSTRAINT CK_ev_cia_status CHECK (status BETWEEN 0 AND 2)
);
GO

GRANT SELECT, INSERT ON dbo.ev_connector_inventory_archives TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_inventory_archives,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_connector_inventory_archives AFTER INSERT;
GO
