-- I8 Production Acceptance — Passo 3 (AB-I8-010): Operational Readiness, Go-Live Authorization & Migration
-- Completion Gate. Materializa CINCO linhas de evidência IMUTÁVEIS e append-only, tenant/project-scoped,
-- tamper-evident (mesmo padrão de 0038/0040/0041/0042/0043): o header de cada versão da decisão de go-live e
-- os controles operacionais/M365 revalidados frescos dentro dela; atestações manuais de critérios de
-- encerramento (§49) sem store dedicado; e o header de cada versão da avaliação de encerramento de migração
-- com os critérios individuais dentro dela. Nenhuma linha destas tabelas é jamais atualizada ou apagada;
-- nenhum efeito real é iniciado em Purview/EXO/Graph/EV/AzCopy/host/tenant M365 por esta migration; nenhum
-- decommission/exclusão destrutiva/revogação irreversível é executado; nenhum projeto/wave é marcado
-- Completed (STOP-THE-LINE do work order).
--   canary_outcome (Domain.Canary.CanaryOutcome): NotPassed=0, CanaryPassed=1
--   go_live_outcome (Domain.GoLive.GoLiveOutcome): Blocked=0, GoLiveAuthorized=1
--   status (Domain.ProductionReadiness.ReadinessControlStatus, reaproveitado por AB-I8-010 para os controles
--     operacionais do go-live e para os critérios de encerramento): NotMeasured=0, NotPerformed=1, Blocked=2,
--     Fail=3, Pass=4
--   evidence_kind (Domain.ProductionReadiness.ReadinessEvidenceKind): None=0, SystemDerived=1, ManualAttestation=2
--   gate_group (Domain.ProductionReadiness.ReadinessGateGroup): Architecture=0, Security=1, Data=2,
--     Operations=3, Microsoft365=4 — go_live_authorization_operational_control_results só grava 3 (Operations)
--     ou 4 (Microsoft365), nunca os demais (aplicado pelo Domain, não recomputado aqui).
--   completion_outcome (Domain.MigrationCompletion.MigrationCompletionOutcome): Blocked=0, Eligible=1
--
-- Aditiva, append-only e não destrutiva: cria CINCO tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0043 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.

-- 1) Header de UMA versão da decisão de go-live (escopo obrigatório item 1) — vincula explicitamente o plano
-- de canário canônico (identidade/versão/fingerprint) e o Production Readiness Review (versão/fingerprint)
-- herdados EXATAMENTE do canário, e materializa o build/commit/digest/policy/capability EXATOS promovidos
-- (escopo obrigatório item 3: same-build/same-policy promotion invariant). canary_outcome_at_authorization e
-- current_readiness_review_*_at_authorization são a evidência FRESCA resolvida no instante da decisão (nunca
-- reaproveitada de um cache antigo) — permitem reidratar e reexecutar o avaliador puro contra as linhas de
-- controle operacionais persistidas (defesa contra adulteração isolada da linha de header).
CREATE TABLE dbo.go_live_authorizations
(
    tenant_id                                             UNIQUEIDENTIFIER NOT NULL,
    project_id                                            UNIQUEIDENTIFIER NOT NULL,
    authorization_version                                 INT              NOT NULL,
    authorization_id                                      UNIQUEIDENTIFIER NOT NULL,
    canary_plan_id                                         UNIQUEIDENTIFIER NOT NULL,
    canary_plan_version                                   INT              NOT NULL,
    canary_plan_fingerprint                               CHAR(64)         NOT NULL,
    readiness_review_version                              INT              NOT NULL,
    readiness_review_fingerprint                          CHAR(64)         NOT NULL,
    build_commit_sha                                      CHAR(40)         NOT NULL,
    build_artifact_digest                                 CHAR(64)         NOT NULL,
    policy_version_fingerprint                            CHAR(64)         NOT NULL,
    capability_matrix_fingerprint                         CHAR(64)         NOT NULL,
    canary_outcome_at_authorization                       TINYINT          NOT NULL,
    current_readiness_review_version_at_authorization     INT              NULL,
    current_readiness_review_fingerprint_at_authorization CHAR(64)         NULL,
    outcome                                                TINYINT          NOT NULL,
    authorization_fingerprint                             CHAR(64)         NOT NULL,
    authorized_by                                         NVARCHAR(200)    NOT NULL,
    authorized_by_role                                    NVARCHAR(50)     NOT NULL,
    correlation_id                                        UNIQUEIDENTIFIER NOT NULL,
    authorized_at_utc                                     DATETIME2(3)     NOT NULL,
    schema_version                                        NVARCHAR(100)    NOT NULL,
    authorization_hash                                    CHAR(64)         NOT NULL,
    CONSTRAINT PK_go_live_authorizations PRIMARY KEY (tenant_id, project_id, authorization_version),
    CONSTRAINT FK_go_live_authorizations_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_go_live_authorizations_version CHECK (authorization_version >= 1),
    CONSTRAINT CK_go_live_authorizations_canary_plan_version CHECK (canary_plan_version >= 1),
    CONSTRAINT CK_go_live_authorizations_readiness_version CHECK (readiness_review_version >= 1),
    CONSTRAINT CK_go_live_authorizations_commit_sha_length CHECK (LEN(build_commit_sha) = 40),
    CONSTRAINT CK_go_live_authorizations_canary_outcome CHECK (canary_outcome_at_authorization BETWEEN 0 AND 1),
    CONSTRAINT CK_go_live_authorizations_outcome CHECK (outcome BETWEEN 0 AND 1),
    -- Ambos os campos de review vigente "no instante da autorização" são NULL juntos (nenhum review composto
    -- ainda) ou preenchidos juntos (nunca um preenchido sem o outro).
    CONSTRAINT CK_go_live_authorizations_current_readiness_pair CHECK (
        (current_readiness_review_version_at_authorization IS NULL AND current_readiness_review_fingerprint_at_authorization IS NULL)
        OR (current_readiness_review_version_at_authorization IS NOT NULL AND current_readiness_review_fingerprint_at_authorization IS NOT NULL))
);
GO

CREATE INDEX IX_go_live_authorizations_scope ON dbo.go_live_authorizations (tenant_id, project_id, authorization_version DESC);
GO

GRANT SELECT, INSERT ON dbo.go_live_authorizations TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.go_live_authorizations,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.go_live_authorizations AFTER INSERT;
GO

-- 2) Controle operacional/M365 individual revalidado FRESCO dentro de UMA versão da decisão de go-live
-- (escopo obrigatório item 4) — mesmo padrão item-table de production_readiness_review_control_results/0042;
-- só grava o subconjunto Operations(3)/Microsoft365(4) do catálogo do Passo 1 (aplicado pelo Domain).
CREATE TABLE dbo.go_live_authorization_operational_control_results
(
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    authorization_version INT             NOT NULL,
    control_id           NVARCHAR(80)     NOT NULL,
    gate_group           TINYINT          NOT NULL,
    status               TINYINT          NOT NULL,
    evidence_kind        TINYINT          NOT NULL,
    evidence_fingerprint CHAR(64)         NOT NULL,
    evidence_locator     NVARCHAR(300)    NOT NULL,
    reason_code          NVARCHAR(200)    NOT NULL,
    observed_at_utc      DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_go_live_authorization_operational_control_results PRIMARY KEY (tenant_id, project_id, authorization_version, control_id),
    CONSTRAINT FK_glaocr_authorization FOREIGN KEY (tenant_id, project_id, authorization_version)
        REFERENCES dbo.go_live_authorizations (tenant_id, project_id, authorization_version),
    CONSTRAINT CK_glaocr_gate_group CHECK (gate_group IN (3, 4)),
    CONSTRAINT CK_glaocr_status CHECK (status BETWEEN 0 AND 4),
    CONSTRAINT CK_glaocr_evidence_kind CHECK (evidence_kind BETWEEN 0 AND 2),
    -- Defesa em profundidade equivalente a ReadinessControlResult.Create: Pass (4) exige evidência real.
    CONSTRAINT CK_glaocr_pass_requires_evidence CHECK (status <> 4 OR evidence_kind <> 0)
);
GO

CREATE INDEX IX_glaocr_scope ON dbo.go_live_authorization_operational_control_results (tenant_id, project_id, authorization_version);
GO

GRANT SELECT, INSERT ON dbo.go_live_authorization_operational_control_results TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.go_live_authorization_operational_control_results,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.go_live_authorization_operational_control_results AFTER INSERT;
GO

-- 3) Atestações manuais de critérios de encerramento §49 Attested (escopo obrigatório item 7/8) — mesmo padrão
-- de production_readiness_control_attestations/0042. Inclui explicitamente "cliente aprovou relatório final"
-- (nunca aprovação implícita por ausência) e "janela de rollback/decommission definida" (registra APENAS a
-- definição — NUNCA dispara ou representa execução de decommission/exclusão destrutiva, escopo obrigatório
-- item 9, STOP-THE-LINE). A validação de que criterion_id pertence ao catálogo E é classificado Attested
-- (nunca SystemDerived) acontece inteiramente no Domain (MigrationCompletionCriterionAttestation.RequireAttestable)
-- ANTES de persistir — esta tabela não reimplementa essa regra.
CREATE TABLE dbo.migration_completion_criterion_attestations
(
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    criterion_id          NVARCHAR(80)     NOT NULL,
    attestation_version   INT              NOT NULL,
    status                TINYINT          NOT NULL,
    evidence_kind         TINYINT          NOT NULL,
    evidence_fingerprint  CHAR(64)         NOT NULL,
    evidence_locator      NVARCHAR(300)    NOT NULL,
    reason_code           NVARCHAR(200)    NOT NULL,
    content_fingerprint   CHAR(64)         NOT NULL,
    submitted_by          NVARCHAR(200)    NOT NULL,
    submitted_by_role     NVARCHAR(50)     NOT NULL,
    correlation_id        UNIQUEIDENTIFIER NOT NULL,
    submitted_at_utc      DATETIME2(3)     NOT NULL,
    schema_version        NVARCHAR(100)    NOT NULL,
    record_hash           CHAR(64)         NOT NULL,
    CONSTRAINT PK_migration_completion_criterion_attestations PRIMARY KEY (tenant_id, project_id, criterion_id, attestation_version),
    CONSTRAINT FK_mcca_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_mcca_status CHECK (status BETWEEN 0 AND 4),
    CONSTRAINT CK_mcca_evidence_kind CHECK (evidence_kind BETWEEN 0 AND 2),
    -- Defesa em profundidade: uma atestação SEMPRE carrega evidência real (nunca None=0) — mesmo padrão de
    -- CK_prca_evidence_kind_never_none/0042.
    CONSTRAINT CK_mcca_evidence_kind_never_none CHECK (evidence_kind <> 0),
    CONSTRAINT CK_mcca_attestation_version CHECK (attestation_version >= 1)
);
GO

CREATE INDEX IX_mcca_scope ON dbo.migration_completion_criterion_attestations (tenant_id, project_id, criterion_id, attestation_version DESC);
GO

GRANT SELECT, INSERT ON dbo.migration_completion_criterion_attestations TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_completion_criterion_attestations,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_completion_criterion_attestations AFTER INSERT;
GO

-- 4) Header de UMA versão da avaliação de encerramento de migração (escopo obrigatório item 7) —
-- anchor_wave_id/anchor_planned_job_name identificam a onda/plano de import job cuja evidência técnica ancora
-- os dois critérios SystemDerived (reconciliação/resultados do provider); nenhuma FK para dbo.migration_waves
-- é exigida (mesmo princípio de dbo.purview_reconciliation_certificates/0038: a evidência de reconciliação já
-- é, por natureza, independente de uma linha materializada em migration_waves).
CREATE TABLE dbo.migration_completion_assessments
(
    tenant_id                UNIQUEIDENTIFIER NOT NULL,
    project_id               UNIQUEIDENTIFIER NOT NULL,
    assessment_version       INT              NOT NULL,
    anchor_wave_id           UNIQUEIDENTIFIER NOT NULL,
    anchor_planned_job_name  NVARCHAR(100)    NOT NULL,
    outcome                  TINYINT          NOT NULL,
    assessment_fingerprint   CHAR(64)         NOT NULL,
    submitted_by             NVARCHAR(200)    NOT NULL,
    submitted_by_role        NVARCHAR(50)     NOT NULL,
    correlation_id           UNIQUEIDENTIFIER NOT NULL,
    generated_at_utc         DATETIME2(3)     NOT NULL,
    schema_version           NVARCHAR(100)    NOT NULL,
    assessment_hash          CHAR(64)         NOT NULL,
    CONSTRAINT PK_migration_completion_assessments PRIMARY KEY (tenant_id, project_id, assessment_version),
    CONSTRAINT FK_mca_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_mca_version CHECK (assessment_version >= 1),
    CONSTRAINT CK_mca_outcome CHECK (outcome BETWEEN 0 AND 1)
);
GO

CREATE INDEX IX_mca_scope ON dbo.migration_completion_assessments (tenant_id, project_id, assessment_version DESC);
GO

GRANT SELECT, INSERT ON dbo.migration_completion_assessments TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_completion_assessments,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_completion_assessments AFTER INSERT;
GO

-- 5) Resultado individual de CADA um dos onze critérios do §49 dentro de UMA versão da avaliação (escopo
-- obrigatório item 8) — mesmo padrão item-table de production_readiness_review_control_results/0042.
CREATE TABLE dbo.migration_completion_assessment_criterion_results
(
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    assessment_version   INT              NOT NULL,
    criterion_id         NVARCHAR(80)     NOT NULL,
    status               TINYINT          NOT NULL,
    evidence_kind        TINYINT          NOT NULL,
    evidence_fingerprint CHAR(64)         NOT NULL,
    evidence_locator     NVARCHAR(300)    NOT NULL,
    reason_code          NVARCHAR(200)    NOT NULL,
    observed_at_utc      DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_migration_completion_assessment_criterion_results PRIMARY KEY (tenant_id, project_id, assessment_version, criterion_id),
    CONSTRAINT FK_mcacr_assessment FOREIGN KEY (tenant_id, project_id, assessment_version)
        REFERENCES dbo.migration_completion_assessments (tenant_id, project_id, assessment_version),
    CONSTRAINT CK_mcacr_status CHECK (status BETWEEN 0 AND 4),
    CONSTRAINT CK_mcacr_evidence_kind CHECK (evidence_kind BETWEEN 0 AND 2),
    -- Defesa em profundidade: Pass (4) exige evidência real (evidence_kind <> None/0).
    CONSTRAINT CK_mcacr_pass_requires_evidence CHECK (status <> 4 OR evidence_kind <> 0)
);
GO

CREATE INDEX IX_mcacr_scope ON dbo.migration_completion_assessment_criterion_results (tenant_id, project_id, assessment_version);
GO

GRANT SELECT, INSERT ON dbo.migration_completion_assessment_criterion_results TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_completion_assessment_criterion_results,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.migration_completion_assessment_criterion_results AFTER INSERT;
GO
