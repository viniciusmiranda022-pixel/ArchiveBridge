-- I6/EPIC-07 Passo 4 — AB-I6-010: workflow de disposition humano/auditável sobre exceções técnicas de
-- reconciliação já materializadas pelo Passo 3 (0036). Transforma uma decisão humana em uma camada
-- auditável POR CIMA do resultado técnico (technical_disposition, NUNCA alterado por uma decisão) — nunca
-- um certificate, ReconciliationOutcome=PASS terminal ou conclusão de wave/projeto (STOP-THE-LINE do work
-- order).
--   item_kind (ReconciliationExceptionItemKind): Pst=0, Archive=1
--   technical_disposition (ReconciliationDisposition): MatchedWithinEvidence=0, Mismatch=1,
--     IncompleteEvidence=2, BlockedIntegrity=3, ExtraInProvider=4 — MatchedWithinEvidence nunca aparece
--     aqui (não é exceção); BlockedIntegrity aparece apenas em linhas nunca inseridas com sucesso, pois a
--     Application recusa fail-closed antes de qualquer INSERT.
--   status (ReconciliationExceptionDecisionStatus): Pending=0 (NUNCA persistido — estado implícito de
--     "nenhuma decisão ainda"), AcceptedException=1, RemediationRequired=2, Rejected=3
--   reason_code (ReconciliationExceptionReasonCode): catálogo fechado 0-6 (ver
--     Domain.TargetIngestion.Purview.Reconciliation.ReconciliationExceptionReasonCode)
--
-- Aditiva, append-only e não destrutiva: cria UMA tabela nova. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0036 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos.

-- Append-only, versionada por exceção (wave, plano, versão de avaliação, item): cada NOVA versão de
-- avaliação (0036) recomeça o histórico de decisão do zero para o mesmo item lógico — uma decisão sobre a
-- versão N nunca se aplica à versão N+1 (item 8: qualquer disposition sobre uma avaliação superseded é
-- recusada fail-closed pela Application/pelo store ANTES de qualquer INSERT aqui). decision_fingerprint é a
-- chave de convergência idempotente (item 9: mesma decisão ⇒ mesma versão); decision_hash cobre TODOS os
-- campos persistidos (tamper-evident, mesmo princípio de assessment_hash em 0036).
CREATE TABLE dbo.purview_reconciliation_exception_dispositions
(
    wave_id                        UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence               INT              NOT NULL,
    assessment_version              INT              NOT NULL,
    item_kind                      TINYINT          NOT NULL,
    item_key                       NVARCHAR(320)    NOT NULL,
    decision_version                INT              NOT NULL,
    tenant_id                      UNIQUEIDENTIFIER NOT NULL,
    project_id                     UNIQUEIDENTIFIER NOT NULL,
    assessment_source_fingerprint  CHAR(64)         NOT NULL,
    technical_disposition          TINYINT          NOT NULL,
    status                         TINYINT          NOT NULL,
    reason_code                    TINYINT          NOT NULL,
    reason_code_catalog_version    TINYINT          NOT NULL,
    comment                        NVARCHAR(500)    NULL,
    decided_by                     NVARCHAR(200)    NOT NULL,
    decided_by_role                NVARCHAR(50)     NOT NULL,
    correlation_id                 UNIQUEIDENTIFIER NOT NULL,
    decided_at_utc                 DATETIME2(3)     NOT NULL,
    decision_fingerprint           CHAR(64)         NOT NULL,
    decision_hash                  CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_reconciliation_exception_dispositions
        PRIMARY KEY (wave_id, attempt_sequence, assessment_version, item_kind, item_key, decision_version),
    CONSTRAINT FK_pred_assessment FOREIGN KEY (wave_id, attempt_sequence, assessment_version, tenant_id, project_id)
        REFERENCES dbo.purview_reconciliation_assessments (wave_id, attempt_sequence, assessment_version, tenant_id, project_id),
    CONSTRAINT CK_pred_item_kind CHECK (item_kind BETWEEN 0 AND 1),
    CONSTRAINT CK_pred_technical_disposition CHECK (technical_disposition BETWEEN 0 AND 4),
    -- status nunca persiste Pending(0) — apenas decisões EXPLÍCITAS chegam a esta tabela.
    CONSTRAINT CK_pred_status CHECK (status BETWEEN 1 AND 3),
    CONSTRAINT CK_pred_reason_code CHECK (reason_code BETWEEN 0 AND 6),
    CONSTRAINT CK_pred_catalog_version CHECK (reason_code_catalog_version >= 1),
    CONSTRAINT CK_pred_decision_version CHECK (decision_version >= 1)
);
GO

-- Resolução da decisão VIGENTE de UMA exceção (GetCurrentAsync/GetHistoryAsync) e do lock de convergência/
-- concorrência de SaveDecisionAsync (WITH UPDLOCK, HOLDLOCK sobre este mesmo índice).
CREATE INDEX IX_pred_exception ON dbo.purview_reconciliation_exception_dispositions
    (tenant_id, project_id, wave_id, attempt_sequence, assessment_version, item_kind, item_key, decision_version DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma decisão é jamais atualizada ou apagada (item 7 do work order:
-- "alteração posterior não pode apagar a decisão anterior").
GRANT SELECT, INSERT ON dbo.purview_reconciliation_exception_dispositions TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_exception_dispositions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_reconciliation_exception_dispositions AFTER INSERT;
GO
