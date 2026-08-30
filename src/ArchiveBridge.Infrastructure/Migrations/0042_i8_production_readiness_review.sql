-- I8 Production Acceptance — Passo 1 (AB-I8-001): Production Readiness Review & Gate Aggregation.
-- Materializa TRÊS linhas de evidência IMUTÁVEIS e append-only, tenant/project-scoped, tamper-evident
-- (mesmo padrão de 0038/0040/0041): atestações manuais de controles processuais/documentais, o header do
-- snapshot agregado do review, e os resultados individuais de CADA controle dentro de um snapshot. Nenhuma
-- linha destas tabelas é jamais atualizada ou apagada; nenhum canário real é iniciado, nenhum host/tenant
-- real é tocado, nenhum projeto/wave é marcado concluído (STOP-THE-LINE do work order).
--   status (Domain.ProductionReadiness.ReadinessControlStatus): NotMeasured=0, NotPerformed=1, Blocked=2,
--     Fail=3, Pass=4
--   evidence_kind (Domain.ProductionReadiness.ReadinessEvidenceKind): None=0, SystemDerived=1, ManualAttestation=2
--   gate_group (Domain.ProductionReadiness.ReadinessGateGroup): Architecture=0, Security=1, Data=2,
--     Operations=3, Microsoft365=4
--   outcome (Domain.ProductionReadiness.ProductionReadinessOutcome): NotReady=0, ReadyForCanary=1 — a
--     COLUNA em si aceita 0/1, mas o ÚNICO caminho de código que pode gravar 1 é
--     ProductionReadinessGateEvaluator.Evaluate quando NENHUM controle do catálogo fixo (32 controles,
--     Domain.ProductionReadiness.ReadinessControlCatalog) está fora de Pass — nunca alegado diretamente.
--
-- Aditiva, append-only e não destrutiva: cria TRÊS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0041 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos.

-- 1) Atestações manuais de controles Attested (escopo obrigatório item 9) — cada linha é a decisão HUMANA
-- explícita de um ator autorizado sobre UM controle que ainda não possui evidência automatizada. A
-- validação de que control_id pertence ao catálogo E é classificado Attested (nunca SystemDerived) acontece
-- inteiramente no Domain (ReadinessControlAttestation.RequireAttestable) ANTES de persistir — esta tabela
-- não reimplementa essa regra.
CREATE TABLE dbo.production_readiness_control_attestations
(
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    control_id            NVARCHAR(80)     NOT NULL,
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
    CONSTRAINT PK_production_readiness_control_attestations PRIMARY KEY (tenant_id, project_id, control_id, attestation_version),
    CONSTRAINT FK_prca_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_prca_status CHECK (status BETWEEN 0 AND 4),
    CONSTRAINT CK_prca_evidence_kind CHECK (evidence_kind BETWEEN 0 AND 2),
    -- Defesa em profundidade: uma atestação SEMPRE carrega evidência real (nunca None=0), mesmo padrão de
    -- ReadinessControlAttestation.Create.
    CONSTRAINT CK_prca_evidence_kind_never_none CHECK (evidence_kind <> 0),
    CONSTRAINT CK_prca_attestation_version CHECK (attestation_version >= 1)
);
GO

CREATE INDEX IX_prca_scope ON dbo.production_readiness_control_attestations
    (tenant_id, project_id, control_id, attestation_version DESC);
GO

GRANT SELECT, INSERT ON dbo.production_readiness_control_attestations TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.production_readiness_control_attestations,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.production_readiness_control_attestations AFTER INSERT;
GO

-- 2) Header do snapshot agregado do review (escopo obrigatório item 8) — UMA linha por versão, cobrindo
-- build/commit revisado, policy/capability fingerprints, outcome agregado e os hashes tamper-evident.
CREATE TABLE dbo.production_readiness_review_snapshots
(
    tenant_id                    UNIQUEIDENTIFIER NOT NULL,
    project_id                   UNIQUEIDENTIFIER NOT NULL,
    review_version               INT              NOT NULL,
    build_commit_sha             CHAR(40)         NOT NULL,
    build_artifact_digest        CHAR(64)         NOT NULL,
    policy_version_fingerprint   CHAR(64)         NOT NULL,
    capability_matrix_fingerprint CHAR(64)        NOT NULL,
    outcome                      TINYINT          NOT NULL,
    review_fingerprint           CHAR(64)         NOT NULL,
    submitted_by                 NVARCHAR(200)    NOT NULL,
    submitted_by_role            NVARCHAR(50)     NOT NULL,
    correlation_id                UNIQUEIDENTIFIER NOT NULL,
    generated_at_utc             DATETIME2(3)     NOT NULL,
    schema_version                NVARCHAR(100)    NOT NULL,
    snapshot_hash                CHAR(64)         NOT NULL,
    CONSTRAINT PK_production_readiness_review_snapshots PRIMARY KEY (tenant_id, project_id, review_version),
    CONSTRAINT FK_prrs_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_prrs_outcome CHECK (outcome BETWEEN 0 AND 1),
    CONSTRAINT CK_prrs_review_version CHECK (review_version >= 1),
    CONSTRAINT CK_prrs_commit_sha_length CHECK (LEN(build_commit_sha) = 40)
);
GO

CREATE INDEX IX_prrs_scope ON dbo.production_readiness_review_snapshots (tenant_id, project_id, review_version DESC);
GO

GRANT SELECT, INSERT ON dbo.production_readiness_review_snapshots TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.production_readiness_review_snapshots,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.production_readiness_review_snapshots AFTER INSERT;
GO

-- 3) Resultado individual de CADA controle do catálogo dentro de UM snapshot (mesmo padrão de item-table de
-- reconciliation assessments/0036) — permite reidratar Rehydrate() com a lista completa de ControlResults e
-- reexecutar o avaliador puro para revalidar outcome/blockers persistidos (defesa contra adulteração
-- isolada da linha de header).
CREATE TABLE dbo.production_readiness_review_control_results
(
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    review_version        INT              NOT NULL,
    control_id            NVARCHAR(80)     NOT NULL,
    gate_group            TINYINT          NOT NULL,
    status                TINYINT          NOT NULL,
    evidence_kind         TINYINT          NOT NULL,
    evidence_fingerprint  CHAR(64)         NOT NULL,
    evidence_locator      NVARCHAR(300)    NOT NULL,
    reason_code           NVARCHAR(200)    NOT NULL,
    observed_at_utc       DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_production_readiness_review_control_results PRIMARY KEY (tenant_id, project_id, review_version, control_id),
    CONSTRAINT FK_prrcr_snapshot FOREIGN KEY (tenant_id, project_id, review_version)
        REFERENCES dbo.production_readiness_review_snapshots (tenant_id, project_id, review_version),
    CONSTRAINT CK_prrcr_gate_group CHECK (gate_group BETWEEN 0 AND 4),
    CONSTRAINT CK_prrcr_status CHECK (status BETWEEN 0 AND 4),
    CONSTRAINT CK_prrcr_evidence_kind CHECK (evidence_kind BETWEEN 0 AND 2),
    -- Defesa em profundidade equivalente a ReadinessControlResult.Create: Pass (4) exige evidência real
    -- (evidence_kind <> None/0) — mesmo um INSERT direto/adulterado fora da store nunca conseguiria
    -- persistir um controle Pass sem evidência.
    CONSTRAINT CK_prrcr_pass_requires_evidence CHECK (status <> 4 OR evidence_kind <> 0)
);
GO

CREATE INDEX IX_prrcr_scope ON dbo.production_readiness_review_control_results (tenant_id, project_id, review_version);
GO

GRANT SELECT, INSERT ON dbo.production_readiness_review_control_results TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.production_readiness_review_control_results,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.production_readiness_review_control_results AFTER INSERT;
GO
