-- Slice 4C (Passo 2) — Enterprise Vault Export Command & Throttling Foundation (AB-4C-005).
--
-- Aditiva e não destrutiva: cria SEIS tabelas novas. Nenhum DROP, nenhum UPDATE de dados, nenhuma
-- redefinição de 0001-0023 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.
--
-- Persiste APENAS metadados estruturais/decisórios: identidade opaca do pedido/tentativa, política efetiva
-- (MaxThreads/MaxPSTSizeMB), resultado estruturado, hash/tamanho de cada output PST calculados após
-- fechamento, lineage para archive/pedido/tentativa e diagnóstico curto de itens oversized (referência
-- opaca do exporter). NUNCA conteúdo de mailbox (assunto, corpo, anexo), credencial EV, token, private key,
-- transcript bruto ou caminho físico absoluto — o local físico é sempre derivado, em tempo de leitura, dos
-- IDs opacos já persistidos aqui (tenant/projeto/connector/pedido/tentativa) mais a raiz autorizada
-- configurada no host do connector; nenhum caminho é gravado.

-- Pedidos de exportação (dbo.ev_export_requests): UM registro por identidade CANÔNICA (AB-4C-005 item 5) —
-- o enfileiramento é idempotente por (tenant, projeto, canonical_idempotency_key); um retry do MESMO pedido
-- lógico (mesmo connector/archive/política) converge para esta MESMA linha, nunca duplica o Job.
CREATE TABLE dbo.ev_export_requests
(
    request_id                 UNIQUEIDENTIFIER NOT NULL,
    job_id                       UNIQUEIDENTIFIER NOT NULL,
    tenant_id                     UNIQUEIDENTIFIER NOT NULL,
    project_id                      UNIQUEIDENTIFIER NOT NULL,
    connector_id                      UNIQUEIDENTIFIER NOT NULL,
    external_archive_id                 NVARCHAR(300)    NOT NULL,
    max_threads                           INT              NOT NULL,
    max_pst_size_mb                         INT              NOT NULL,
    requested_by                              NVARCHAR(200)    NOT NULL,
    correlation_id                              UNIQUEIDENTIFIER NOT NULL,
    canonical_idempotency_key                     UNIQUEIDENTIFIER NOT NULL,
    created_at_utc                                  DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_export_requests PRIMARY KEY (request_id),
    CONSTRAINT UQ_ev_export_requests_scope UNIQUE (request_id, tenant_id, project_id),
    CONSTRAINT UQ_ev_export_requests_job UNIQUE (job_id),
    CONSTRAINT UQ_ev_export_requests_idempotency UNIQUE (tenant_id, project_id, canonical_idempotency_key),
    CONSTRAINT FK_ev_export_requests_connector FOREIGN KEY (connector_id, tenant_id, project_id)
        REFERENCES dbo.ev_connectors (connector_id, tenant_id, project_id),
    -- Envelope documentado do cmdlet (runbook §16.3) reforçado também no BANCO — defesa em profundidade da
    -- MESMA regra do Domain (ExportRequestPolicy.Create).
    CONSTRAINT CK_ev_export_requests_max_threads CHECK (max_threads BETWEEN 1 AND 32),
    CONSTRAINT CK_ev_export_requests_max_pst_size CHECK (max_pst_size_mb BETWEEN 500 AND 51200)
);
GO

GRANT SELECT, INSERT ON dbo.ev_export_requests TO ab_app_role;
GO
-- A identidade de MANUTENÇÃO precisa enxergar esta tabela: SqlEvExportPendingScopeReader (mesmo padrão de
-- SqlEvDiscoveryPendingScopeReader) faz EXISTS(...) sobre dbo.ev_export_requests para enumerar escopos
-- elegíveis entre tenants — sem este GRANT, a leitura de manutenção falha fechado por permissão negada.
GRANT SELECT ON dbo.ev_export_requests TO ab_maintenance_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_requests,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_requests AFTER INSERT;
GO

-- Leases de throttling (dbo.ev_export_throttle_leases, AB-4C-005 item 4): impede concorrência não
-- autorizada por CONNECTOR e por ARCHIVE simultaneamente. Os DOIS índices únicos FILTRADOS
-- (WHERE released_at_utc IS NULL) sobre a MESMA linha são o backstop atômico: um único INSERT só sucede se
-- NENHUM dos dois já estiver em uso — nunca uma aquisição parcial (só connector, ou só archive).
CREATE TABLE dbo.ev_export_throttle_leases
(
    lease_id           UNIQUEIDENTIFIER NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id             UNIQUEIDENTIFIER NOT NULL,
    connector_id              UNIQUEIDENTIFIER NOT NULL,
    external_archive_id         NVARCHAR(300)    NOT NULL,
    attempt_id                    UNIQUEIDENTIFIER NOT NULL,
    correlation_id                   UNIQUEIDENTIFIER NOT NULL,
    acquired_at_utc                    DATETIME2(3)     NOT NULL,
    released_at_utc                      DATETIME2(3)     NULL,
    CONSTRAINT PK_ev_export_throttle_leases PRIMARY KEY (lease_id),
    CONSTRAINT CK_ev_export_throttle_leases_released CHECK (released_at_utc IS NULL OR released_at_utc >= acquired_at_utc)
);
GO

CREATE UNIQUE INDEX UX_ev_export_throttle_leases_connector
    ON dbo.ev_export_throttle_leases (connector_id) WHERE released_at_utc IS NULL;
GO

CREATE UNIQUE INDEX UX_ev_export_throttle_leases_archive
    ON dbo.ev_export_throttle_leases (tenant_id, project_id, external_archive_id) WHERE released_at_utc IS NULL;
GO

-- Aplicação recebe INSERT e UPDATE restrito a released_at_utc (liberação do lease) — nenhum outro campo
-- muda depois de adquirido.
GRANT SELECT, INSERT ON dbo.ev_export_throttle_leases TO ab_app_role;
GO
GRANT UPDATE (released_at_utc) ON dbo.ev_export_throttle_leases TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_throttle_leases,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_throttle_leases AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_throttle_leases AFTER UPDATE;
GO

-- Tentativas de exportação (dbo.ev_export_attempts, AB-4C-005 items 11/12): append-only — evidência
-- anterior NUNCA é reescrita. UNIQUE (request_id, attempt_number) é o backstop de idempotência: sob
-- fencing, um retry da MESMA tentativa (mesmo número) nunca duplica a linha.
CREATE TABLE dbo.ev_export_attempts
(
    attempt_id            UNIQUEIDENTIFIER NOT NULL,
    request_id              UNIQUEIDENTIFIER NOT NULL,
    tenant_id                 UNIQUEIDENTIFIER NOT NULL,
    project_id                  UNIQUEIDENTIFIER NOT NULL,
    connector_id                   UNIQUEIDENTIFIER NOT NULL,
    external_archive_id              NVARCHAR(300)    NOT NULL,
    attempt_number                     INT              NOT NULL,
    outcome                              TINYINT          NOT NULL, -- 0 Completed,1 Failed,2 CapabilityBlocked,3 Throttled,4 IntegrityFailed
    result_code                           NVARCHAR(64)     NOT NULL,
    blocking_reason                         NVARCHAR(300)    NULL,
    engine_version                            NVARCHAR(100)    NULL,
    connector_version                           NVARCHAR(50)     NULL,
    manifest_hash                                 CHAR(64)         NULL,
    process_exit_code                               INT              NULL,
    started_at_utc                                    DATETIME2(3)     NOT NULL,
    completed_at_utc                                    DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_export_attempts PRIMARY KEY (attempt_id),
    CONSTRAINT UQ_ev_export_attempts_scope UNIQUE (attempt_id, tenant_id, project_id),
    CONSTRAINT FK_ev_export_attempts_request FOREIGN KEY (request_id, tenant_id, project_id)
        REFERENCES dbo.ev_export_requests (request_id, tenant_id, project_id),
    CONSTRAINT CK_ev_export_attempts_outcome CHECK (outcome BETWEEN 0 AND 4),
    CONSTRAINT CK_ev_export_attempts_attempt_number CHECK (attempt_number >= 1),
    CONSTRAINT CK_ev_export_attempts_timestamps CHECK (completed_at_utc >= started_at_utc),
    -- Manifesto/engine só existem quando a tentativa foi Completed (0) — reforçado no BANCO como defesa em
    -- profundidade da mesma regra do Domain (EvExportRunResult.IsSuccessful).
    CONSTRAINT CK_ev_export_attempts_manifest_only_when_completed CHECK (
        (outcome = 0 AND manifest_hash IS NOT NULL AND engine_version IS NOT NULL)
        OR (outcome <> 0 AND manifest_hash IS NULL)),
    CONSTRAINT CK_ev_export_attempts_blocking_reason CHECK (
        (outcome IN (2, 4) AND blocking_reason IS NOT NULL) OR (outcome NOT IN (2, 4))),
    CONSTRAINT UX_ev_export_attempts_number UNIQUE (request_id, attempt_number)
);
GO

CREATE INDEX IX_ev_export_attempts_request ON dbo.ev_export_attempts (tenant_id, project_id, request_id, completed_at_utc DESC);
GO

GRANT SELECT, INSERT ON dbo.ev_export_attempts TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_attempts,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_attempts AFTER INSERT;
GO

-- Manifesto canônico dos PSTs exportados (dbo.ev_export_manifest_entries, item 12): filha append-only de
-- ev_export_attempts. output_id é a identidade OPACA derivada do hash+tamanho pelo Domain — nunca o nome
-- de arquivo (item 10); file_name_hint é preservado só como evidência operacional.
CREATE TABLE dbo.ev_export_manifest_entries
(
    attempt_id             UNIQUEIDENTIFIER NOT NULL,
    tenant_id                 UNIQUEIDENTIFIER NOT NULL,
    project_id                  UNIQUEIDENTIFIER NOT NULL,
    output_id                     UNIQUEIDENTIFIER NOT NULL,
    file_name_hint                   NVARCHAR(260)    NOT NULL,
    size_bytes                         BIGINT           NOT NULL,
    content_sha256                       CHAR(64)         NOT NULL,
    CONSTRAINT PK_ev_export_manifest_entries PRIMARY KEY (attempt_id, output_id),
    CONSTRAINT FK_ev_export_manifest_entries_attempt FOREIGN KEY (attempt_id, tenant_id, project_id)
        REFERENCES dbo.ev_export_attempts (attempt_id, tenant_id, project_id),
    CONSTRAINT CK_ev_export_manifest_entries_size CHECK (size_bytes >= 0)
);
GO

GRANT SELECT, INSERT ON dbo.ev_export_manifest_entries TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_manifest_entries,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_manifest_entries AFTER INSERT;
GO

-- Itens nativos oversized (dbo.ev_export_oversized_items, §16.4, item 14): filha append-only de
-- ev_export_attempts — NUNCA omitidos do certificado final. external_item_reference é uma referência OPACA
-- emitida pelo exporter (nunca assunto/remetente/destinatário/corpo/anexo).
CREATE TABLE dbo.ev_export_oversized_items
(
    oversized_item_id        UNIQUEIDENTIFIER NOT NULL,
    attempt_id                 UNIQUEIDENTIFIER NOT NULL,
    tenant_id                    UNIQUEIDENTIFIER NOT NULL,
    project_id                     UNIQUEIDENTIFIER NOT NULL,
    external_item_reference          NVARCHAR(300)    NOT NULL,
    size_bytes                         BIGINT           NOT NULL,
    classification                       TINYINT          NOT NULL, -- 0 ExceedsPstItemThreshold,1 ExceedsNativeExportThreshold
    diagnostic                             NVARCHAR(300)    NULL,
    detected_at_utc                          DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_export_oversized_items PRIMARY KEY (oversized_item_id),
    CONSTRAINT FK_ev_export_oversized_items_attempt FOREIGN KEY (attempt_id, tenant_id, project_id)
        REFERENCES dbo.ev_export_attempts (attempt_id, tenant_id, project_id),
    CONSTRAINT CK_ev_export_oversized_items_classification CHECK (classification BETWEEN 0 AND 1),
    -- Limiar documentado (§16.4): nunca um item de 250 MB ou menos é aceito como oversized.
    CONSTRAINT CK_ev_export_oversized_items_threshold CHECK (size_bytes > 262144000)
);
GO

GRANT SELECT, INSERT ON dbo.ev_export_oversized_items TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_oversized_items,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_oversized_items AFTER INSERT;
GO

-- Custódia/auditoria do ciclo de vida de exportação (dbo.ev_export_events, item 17): append-only, sem
-- conteúdo de mailbox/credencial/transcript bruto — apenas o código do evento e um detalhe curto.
CREATE TABLE dbo.ev_export_events
(
    event_id          UNIQUEIDENTIFIER NOT NULL,
    request_id          UNIQUEIDENTIFIER NOT NULL,
    tenant_id              UNIQUEIDENTIFIER NOT NULL,
    project_id               UNIQUEIDENTIFIER NOT NULL,
    attempt_id                  UNIQUEIDENTIFIER NULL,
    event_code                    TINYINT          NOT NULL, -- 0 Requested,1 Started,2 Throttled,3 RetryScheduled,4 Completed,5 Failed,6 OutputDiscovered,7 OversizedDetected,8 IntegrityFailed
    detail                           NVARCHAR(300)    NULL,
    correlation_id                     UNIQUEIDENTIFIER NOT NULL,
    occurred_at_utc                      DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_export_events PRIMARY KEY (event_id),
    CONSTRAINT FK_ev_export_events_request FOREIGN KEY (request_id, tenant_id, project_id)
        REFERENCES dbo.ev_export_requests (request_id, tenant_id, project_id),
    CONSTRAINT CK_ev_export_events_code CHECK (event_code BETWEEN 0 AND 8)
);
GO

CREATE INDEX IX_ev_export_events_request ON dbo.ev_export_events (tenant_id, project_id, request_id, occurred_at_utc ASC);
GO

GRANT SELECT, INSERT ON dbo.ev_export_events TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_events,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_export_events AFTER INSERT;
GO
