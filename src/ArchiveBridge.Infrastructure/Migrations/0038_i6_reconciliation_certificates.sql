-- I6/EPIC-07 Passo 5 — AB-I6-013: reconciliation certificate. Último item documentado do EPIC-07: materializa
-- de forma imutável, determinística e tamper-evident o resultado técnico de reconciliação de uma wave
-- (avaliação canônica do Passo 3 + dispositions humanas vigentes do Passo 4) — NUNCA marca wave/projeto
-- COMPLETED, NUNCA é sign-off final/cliente, NUNCA escreve em Purview/EXO/Graph/EV (STOP-THE-LINE do work
-- order).
--   result (Domain.Reconciliation.ReconciliationOutcome): Pass=0, PassWithExplainedExceptions=1,
--     Inconclusive=2, Fail=3, DuplicateRisk=4
--   event_type (ReconciliationCertificateAuditEventType): Issued=0, Converged=1, Verified=2,
--     IntegrityViolationDetected=3, Superseded=4
--
-- Aditiva, append-only e não destrutiva: cria DUAS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0037 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos.
--
-- AB-I6-014 (revisão pré-merge deste PR): decisions_state_fingerprint materializa no schema o fingerprint
-- das dispositions vigentes (ReconciliationExceptionDecisionsStateHash) que antes só existia como valor de
-- fencing efêmero na store — agora participa de certificate_hash/evaluation_fingerprint (v2), então uma
-- disposition alterada que preserve a mesma classificação de desvio ainda assim invalida replay/convergência
-- e marca o certificate anterior stale. Ajuste seguro porque este PR ainda não foi mergeado (0038 nunca
-- existiu em produção).

-- Append-only, versionado por (onda, plano de import job): a MESMA impressão digital de avaliação
-- (evaluation_fingerprint, item 16) converge para a MESMA certificate_version (replay idempotente); uma
-- mudança REAL na evidência canônica (nova avaliação, disposition nova/alterada, ou sinal de duplicidade)
-- produz uma nova versão — nunca sobrescreve uma anterior (item 9/16-18 do work order: sem overwrite
-- silencioso, histórico/supersession sempre preservado). certificate_hash cobre TODOS os campos persistidos
-- (tamper-evident, mesmo princípio de assessment_hash em 0036/decision_hash em 0037).
CREATE TABLE dbo.purview_reconciliation_certificates
(
    wave_id                        UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence               INT              NOT NULL,
    certificate_version            INT              NOT NULL,
    tenant_id                      UNIQUEIDENTIFIER NOT NULL,
    project_id                     UNIQUEIDENTIFIER NOT NULL,
    assessment_version              INT              NOT NULL,
    assessment_source_fingerprint  CHAR(64)         NOT NULL,
    mapping_fingerprint            CHAR(64)         NOT NULL,
    result                         TINYINT          NOT NULL,
    total_item_count               INT              NOT NULL,
    incomplete_item_count          INT              NOT NULL,
    deviation_count                INT              NOT NULL,
    deviations_sha256              CHAR(64)         NOT NULL,
    decisions_state_fingerprint    CHAR(64)         NOT NULL,
    duplicate_risk_detected        BIT              NOT NULL,
    evaluation_fingerprint         CHAR(64)         NOT NULL,
    issued_by                      NVARCHAR(200)    NOT NULL,
    issued_by_role                 NVARCHAR(50)     NOT NULL,
    correlation_id                 UNIQUEIDENTIFIER NOT NULL,
    generated_at_utc               DATETIME2(3)     NOT NULL,
    schema_version                 NVARCHAR(100)    NOT NULL,
    certificate_hash               CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_reconciliation_certificates
        PRIMARY KEY (wave_id, attempt_sequence, certificate_version),
    CONSTRAINT FK_prc_assessment FOREIGN KEY (wave_id, attempt_sequence, assessment_version, tenant_id, project_id)
        REFERENCES dbo.purview_reconciliation_assessments (wave_id, attempt_sequence, assessment_version, tenant_id, project_id),
    CONSTRAINT CK_prc_result CHECK (result BETWEEN 0 AND 4),
    CONSTRAINT CK_prc_certificate_version CHECK (certificate_version >= 1),
    CONSTRAINT CK_prc_total_item_count CHECK (total_item_count >= 0),
    CONSTRAINT CK_prc_incomplete_item_count CHECK (incomplete_item_count >= 0 AND incomplete_item_count <= total_item_count),
    CONSTRAINT CK_prc_deviation_count CHECK (deviation_count >= 0)
);
GO

-- Resolução do certificate VIGENTE (GetLatestAsync/GetByVersionAsync/GetHistoryAsync) e do lock de
-- convergência/concorrência de IssueOrConvergeAsync (WITH UPDLOCK, HOLDLOCK sobre este mesmo índice).
CREATE INDEX IX_prc_certificate ON dbo.purview_reconciliation_certificates
    (tenant_id, project_id, wave_id, attempt_sequence, certificate_version DESC);
GO

-- Resolução de GetLatestForWaveAcrossOtherAttemptsAsync (detecção de DUPLICATE_RISK entre tentativas
-- distintas da MESMA onda) — não inclui attempt_sequence, pois a consulta varre TODAS as tentativas da onda.
CREATE INDEX IX_prc_wave ON dbo.purview_reconciliation_certificates
    (tenant_id, project_id, wave_id, certificate_version DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhum certificate é jamais atualizado ou apagado (item 9 do work
-- order: "nenhum overwrite silencioso de certificate anterior").
GRANT SELECT, INSERT ON dbo.purview_reconciliation_certificates TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_certificates,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_certificates AFTER INSERT;
GO

-- Trilha auditável append-only de eventos sobre um certificate (item 20 do work order: emissão, replay,
-- verificação, supersession e falha de integridade). Autocontida (planned_job_name como texto, sem FK) para
-- permanecer sempre insertável mesmo quando o evento é uma falha de integridade sobre uma referência que a
-- própria falha impede de resolver plenamente. Nunca contém segredo ou PII indevida — apenas metadados
-- técnicos necessários à responsabilização.
CREATE TABLE dbo.purview_reconciliation_certificate_audit_events
(
    event_id                       BIGINT IDENTITY(1,1) NOT NULL,
    tenant_id                      UNIQUEIDENTIFIER NOT NULL,
    project_id                     UNIQUEIDENTIFIER NOT NULL,
    wave_id                        UNIQUEIDENTIFIER NOT NULL,
    planned_job_name               VARCHAR(100)     NOT NULL,
    certificate_version            INT              NULL,
    event_type                     TINYINT          NOT NULL,
    actor_id                       NVARCHAR(200)    NOT NULL,
    actor_role                     NVARCHAR(50)     NOT NULL,
    succeeded                      BIT              NOT NULL,
    reason                         NVARCHAR(500)    NOT NULL,
    correlation_id                 UNIQUEIDENTIFIER NOT NULL,
    occurred_at_utc                DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_purview_reconciliation_certificate_audit_events PRIMARY KEY (event_id),
    CONSTRAINT CK_prcae_event_type CHECK (event_type BETWEEN 0 AND 4),
    CONSTRAINT CK_prcae_certificate_version CHECK (certificate_version IS NULL OR certificate_version >= 1)
);
GO

CREATE INDEX IX_prcae_scope ON dbo.purview_reconciliation_certificate_audit_events
    (tenant_id, project_id, wave_id, occurred_at_utc DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhum evento de auditoria é jamais atualizado ou apagado.
GRANT SELECT, INSERT ON dbo.purview_reconciliation_certificate_audit_events TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_certificate_audit_events,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_certificate_audit_events AFTER INSERT;
GO
