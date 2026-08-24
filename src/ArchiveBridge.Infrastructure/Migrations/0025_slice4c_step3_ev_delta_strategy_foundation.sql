-- Slice 4C (Passo 3) — Enterprise Vault Delta Strategy & Freeze Planning Foundation (AB-4C-008).
--
-- Aditiva e não destrutiva: cria QUATRO tabelas novas. Nenhum DROP, nenhum UPDATE de dados, nenhuma
-- redefinição de 0001-0024 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.
--
-- Persiste APENAS metadados estruturais/decisórios de baseline/delta/freeze: identidade opaca de
-- execução/tentativa/watermark, fase, strategy (nome+versão) resolvida, token OPACO do watermark (emitido
-- e interpretado exclusivamente pelo adapter EV — nunca decodificado pelo Control Plane), lineage e estado
-- de autorização de freeze. NUNCA conteúdo de mailbox, credencial EV, token de acesso, private key ou
-- transcript bruto.

-- Watermarks (dbo.ev_watermarks, req 3/4/5): append-only — evidência anterior NUNCA é reescrita. O
-- conteúdo de opaque_token é emitido e interpretado EXCLUSIVAMENTE pelo adapter EV da strategy que o
-- produziu; Domain/Application/Control Plane nunca o decodificam. lineage_hash é o backstop de
-- adulteração calculado pelo Domain (EvWatermark.Rehydrate revalida contra os campos realmente
-- carregados).
CREATE TABLE dbo.ev_watermarks
(
    watermark_id                UNIQUEIDENTIFIER NOT NULL,
    tenant_id                     UNIQUEIDENTIFIER NOT NULL,
    project_id                      UNIQUEIDENTIFIER NOT NULL,
    connector_id                       UNIQUEIDENTIFIER NOT NULL,
    external_archive_id                  NVARCHAR(300)    NOT NULL,
    phase                                  TINYINT          NOT NULL, -- 0 Baseline,1 Delta,2 FinalDelta
    strategy_name                            NVARCHAR(100)    NOT NULL,
    strategy_version                           INT              NOT NULL,
    producing_execution_id                       UNIQUEIDENTIFIER NOT NULL,
    opaque_token                                   NVARCHAR(4000)   NOT NULL,
    lineage_hash                                     CHAR(64)         NOT NULL,
    issued_at_utc                                      DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_watermarks PRIMARY KEY (watermark_id),
    CONSTRAINT UQ_ev_watermarks_scope UNIQUE (watermark_id, tenant_id, project_id),
    CONSTRAINT CK_ev_watermarks_phase CHECK (phase BETWEEN 0 AND 2),
    CONSTRAINT CK_ev_watermarks_strategy_version CHECK (strategy_version >= 1),
    CONSTRAINT FK_ev_watermarks_connector FOREIGN KEY (connector_id, tenant_id, project_id)
        REFERENCES dbo.ev_connectors (connector_id, tenant_id, project_id)
);
GO

CREATE INDEX IX_ev_watermarks_latest
    ON dbo.ev_watermarks (tenant_id, project_id, connector_id, external_archive_id, issued_at_utc DESC);
GO

GRANT SELECT, INSERT ON dbo.ev_watermarks TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_watermarks,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_watermarks AFTER INSERT;
GO

-- Tentativas de execução de fase de delta (dbo.ev_delta_attempts, req 1/5/12/14): append-only — evidência
-- anterior NUNCA é reescrita. canonical_idempotency_key é a identidade CANÔNICA computada pelo Domain
-- (EvDeltaRunIdentity, SEM a strategy — decisão derivada, não entrada do pedido): o MESMO
-- phase+watermark-anterior+archive converge SEMPRE para o MESMO run_id. UX_ev_delta_attempts_number é o
-- backstop de concorrência: duas gravações concorrentes calculando o MESMO próximo attempt_number para a
-- MESMA chave nunca duplicam a linha vencedora (mesmo padrão de ev_connector_inventory_snapshots).
CREATE TABLE dbo.ev_delta_attempts
(
    attempt_id                  UNIQUEIDENTIFIER NOT NULL,
    run_id                        UNIQUEIDENTIFIER NOT NULL,
    tenant_id                       UNIQUEIDENTIFIER NOT NULL,
    project_id                        UNIQUEIDENTIFIER NOT NULL,
    connector_id                         UNIQUEIDENTIFIER NOT NULL,
    external_archive_id                     NVARCHAR(300)    NOT NULL,
    phase                                     TINYINT          NOT NULL, -- 0 Baseline,1 Delta,2 FinalDelta
    canonical_idempotency_key                   UNIQUEIDENTIFIER NOT NULL,
    attempt_number                                INT              NOT NULL,
    strategy_name                                   NVARCHAR(100)    NULL,
    strategy_version                                  INT              NULL,
    previous_watermark_id                               UNIQUEIDENTIFIER NULL,
    issued_watermark_id                                   UNIQUEIDENTIFIER NULL,
    outcome                                                 TINYINT          NOT NULL, -- 0 Completed,1 Failed,2 StrategyUnsupported,3 WatermarkRejected
    blocking_reason                                           NVARCHAR(300)    NULL,
    started_at_utc                                              DATETIME2(3)     NOT NULL,
    completed_at_utc                                              DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_delta_attempts PRIMARY KEY (attempt_id),
    CONSTRAINT UQ_ev_delta_attempts_scope UNIQUE (attempt_id, tenant_id, project_id),
    CONSTRAINT CK_ev_delta_attempts_phase CHECK (phase BETWEEN 0 AND 2),
    CONSTRAINT CK_ev_delta_attempts_outcome CHECK (outcome BETWEEN 0 AND 3),
    CONSTRAINT CK_ev_delta_attempts_attempt_number CHECK (attempt_number >= 1),
    CONSTRAINT CK_ev_delta_attempts_timestamps CHECK (completed_at_utc >= started_at_utc),
    CONSTRAINT CK_ev_delta_attempts_strategy_pair CHECK (
        (strategy_name IS NULL AND strategy_version IS NULL) OR (strategy_name IS NOT NULL AND strategy_version IS NOT NULL)),
    -- StrategyUnsupported (2) é bloqueado ANTES de qualquer seleção bem-sucedida — nunca tem strategy resolvida.
    CONSTRAINT CK_ev_delta_attempts_strategy_unsupported CHECK (
        (outcome = 2 AND strategy_name IS NULL) OR (outcome <> 2)),
    -- Watermark emitido só existe quando a tentativa foi Completed (0) — defesa em profundidade da regra do Domain.
    CONSTRAINT CK_ev_delta_attempts_watermark_only_when_completed CHECK (
        (outcome = 0 AND issued_watermark_id IS NOT NULL AND strategy_name IS NOT NULL)
        OR (outcome <> 0 AND issued_watermark_id IS NULL)),
    CONSTRAINT CK_ev_delta_attempts_blocking_reason CHECK (
        (outcome IN (2, 3) AND blocking_reason IS NOT NULL) OR (outcome NOT IN (2, 3))),
    CONSTRAINT UX_ev_delta_attempts_number UNIQUE (tenant_id, project_id, canonical_idempotency_key, attempt_number),
    CONSTRAINT FK_ev_delta_attempts_connector FOREIGN KEY (connector_id, tenant_id, project_id)
        REFERENCES dbo.ev_connectors (connector_id, tenant_id, project_id),
    CONSTRAINT FK_ev_delta_attempts_previous_watermark FOREIGN KEY (previous_watermark_id, tenant_id, project_id)
        REFERENCES dbo.ev_watermarks (watermark_id, tenant_id, project_id),
    CONSTRAINT FK_ev_delta_attempts_issued_watermark FOREIGN KEY (issued_watermark_id, tenant_id, project_id)
        REFERENCES dbo.ev_watermarks (watermark_id, tenant_id, project_id)
);
GO

CREATE INDEX IX_ev_delta_attempts_run ON dbo.ev_delta_attempts (tenant_id, project_id, run_id, attempt_number DESC);
GO
CREATE INDEX IX_ev_delta_attempts_idempotency
    ON dbo.ev_delta_attempts (tenant_id, project_id, canonical_idempotency_key, attempt_number DESC);
GO

GRANT SELECT, INSERT ON dbo.ev_delta_attempts TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_delta_attempts,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_delta_attempts AFTER INSERT;
GO

-- Plano de freeze/cutover (dbo.ev_freeze_plans, req 9/10/11): UMA linha MUTÁVEL de estado atual por
-- archive (mesmo desenho de "propriedade atual com concorrência otimista" dos slots de throttling do
-- Passo 2) — nunca representa execução real, apenas estado e autorização formal.
-- EvFreezeStatus.NotRequested nunca é persistido (é o estado "nenhuma linha ainda"); status vigente
-- começa em 1 (FreezeRequired). version é o controle de concorrência otimista: toda atualização exige o
-- version ANTERIOR à transição aplicada em memória — um version divergente é gravação concorrente,
-- recusada fail-closed pela Application (0 linhas afetadas ⇒ ConcurrencyException).
CREATE TABLE dbo.ev_freeze_plans
(
    plan_id                       UNIQUEIDENTIFIER NOT NULL,
    tenant_id                       UNIQUEIDENTIFIER NOT NULL,
    project_id                        UNIQUEIDENTIFIER NOT NULL,
    connector_id                         UNIQUEIDENTIFIER NOT NULL,
    external_archive_id                     NVARCHAR(300)    NOT NULL,
    status                                    TINYINT          NOT NULL, -- 1 FreezeRequired,2 FreezeAuthorized,3 FreezeRejected,4 FinalDeltaReady,5 RollbackRetentionRequired,6 DecommissionBlocked
    version                                     INT              NOT NULL,
    authorized_by                                 NVARCHAR(200)    NULL,
    authorized_role                                 TINYINT          NULL, -- 1 MigrationOperator,2 TenantAdministrator (0/Unspecified nunca persistido)
    justification                                     NVARCHAR(2000)   NULL,
    authorization_correlation_id                        UNIQUEIDENTIFIER NULL,
    authorized_at_utc                                     DATETIME2(3)     NULL,
    created_at_utc                                          DATETIME2(3)     NOT NULL,
    updated_at_utc                                            DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_freeze_plans PRIMARY KEY (plan_id),
    CONSTRAINT UQ_ev_freeze_plans_archive UNIQUE (tenant_id, project_id, connector_id, external_archive_id),
    CONSTRAINT CK_ev_freeze_plans_status CHECK (status BETWEEN 1 AND 6),
    CONSTRAINT CK_ev_freeze_plans_version CHECK (version >= 1),
    CONSTRAINT CK_ev_freeze_plans_role_specified CHECK (authorized_role IS NULL OR authorized_role BETWEEN 1 AND 2),
    CONSTRAINT CK_ev_freeze_plans_authorization_fields CHECK (
        (status IN (2, 4, 5, 6) AND authorized_by IS NOT NULL AND authorized_role IS NOT NULL AND justification IS NOT NULL
             AND authorization_correlation_id IS NOT NULL AND authorized_at_utc IS NOT NULL)
        OR (status IN (1, 3) AND authorized_by IS NULL AND authorized_role IS NULL AND justification IS NULL
             AND authorization_correlation_id IS NULL AND authorized_at_utc IS NULL)),
    CONSTRAINT FK_ev_freeze_plans_connector FOREIGN KEY (connector_id, tenant_id, project_id)
        REFERENCES dbo.ev_connectors (connector_id, tenant_id, project_id)
);
GO

GRANT SELECT, INSERT ON dbo.ev_freeze_plans TO ab_app_role;
GO
GRANT UPDATE (status, version, authorized_by, authorized_role, justification, authorization_correlation_id, authorized_at_utc, updated_at_utc)
    ON dbo.ev_freeze_plans TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_freeze_plans,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_freeze_plans AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_freeze_plans AFTER UPDATE;
GO

-- Custódia/auditoria do ciclo de vida de delta/freeze (dbo.ev_delta_events, req 15): append-only, sem
-- conteúdo de mailbox/credencial/transcript bruto — apenas o código do evento e um detalhe curto. run_id
-- e freeze_plan_id são mutuamente contextuais (um evento de execução referencia run_id; um evento de
-- freeze referencia freeze_plan_id) — nenhuma FK: um evento de bloqueio ANTES de qualquer run/plan
-- persistido (ex.: strategy-selected na primeiríssima tentativa) referencia ambos como NULL.
CREATE TABLE dbo.ev_delta_events
(
    event_id             UNIQUEIDENTIFIER NOT NULL,
    tenant_id               UNIQUEIDENTIFIER NOT NULL,
    project_id                 UNIQUEIDENTIFIER NOT NULL,
    run_id                        UNIQUEIDENTIFIER NULL,
    watermark_id                     UNIQUEIDENTIFIER NULL,
    freeze_plan_id                      UNIQUEIDENTIFIER NULL,
    event_code                             TINYINT          NOT NULL, -- 0 StrategySelected,1 BaselineStarted,2 BaselineCompleted,3 DeltaRequested,4 DeltaCompleted,5 DeltaFailed,6 WatermarkIssued,7 WatermarkAccepted,8 WatermarkRejected,9 FreezeRequested,10 FreezeAuthorized,11 FreezeRejected,12 FinalDeltaReady,13 DecommissionBlocked
    detail                                    NVARCHAR(300)    NULL,
    correlation_id                              UNIQUEIDENTIFIER NOT NULL,
    occurred_at_utc                               DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_ev_delta_events PRIMARY KEY (event_id),
    CONSTRAINT CK_ev_delta_events_code CHECK (event_code BETWEEN 0 AND 13)
);
GO

CREATE INDEX IX_ev_delta_events_run ON dbo.ev_delta_events (tenant_id, project_id, run_id, occurred_at_utc ASC);
GO
CREATE INDEX IX_ev_delta_events_freeze_plan ON dbo.ev_delta_events (tenant_id, project_id, freeze_plan_id, occurred_at_utc ASC);
GO

GRANT SELECT, INSERT ON dbo.ev_delta_events TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_delta_events,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.ev_delta_events AFTER INSERT;
GO
