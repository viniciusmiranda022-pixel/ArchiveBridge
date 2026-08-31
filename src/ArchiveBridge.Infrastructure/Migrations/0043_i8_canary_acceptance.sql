-- I8 Production Acceptance — Passo 2 (AB-I8-004): Production Canary Acceptance & Evidence.
-- Materializa DUAS linhas de evidência IMUTÁVEIS e append-only, tenant/project-scoped, tamper-evident (mesmo
-- padrão de 0038/0040/0041/0042): o header de cada versão do plano de canário, e cada resultado submetido
-- (atestação de operador, resolução SystemDerived, ou decisão de aprovação) de cada cenário do catálogo
-- dentro de uma versão do plano. Nenhuma linha destas tabelas é jamais atualizada ou apagada; nenhum canário
-- real é iniciado, nenhum host/tenant real é tocado por esta migration, nenhum projeto/wave é marcado
-- concluído (STOP-THE-LINE do work order).
--   status (Domain.Canary.CanaryScenarioStatus): Pending=0, Running=1, NotPerformed=2, Blocked=3, Fail=4,
--     Pass=5
--   evidence_kind (Domain.Canary.CanaryEvidenceKind): None=0, SystemDerived=1, OperatorAttestation=2,
--     HumanApprovalDecision=3
--
-- Aditiva, append-only e não destrutiva: cria DUAS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0042 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos.

-- 1) Header de UMA versão do plano de canário (escopo obrigatório item 1) — vincula explicitamente o
-- Production Readiness Review canônico (versão + fingerprint) e o build/digest/policy/capability EXATOS sob
-- canário. plan_id é a identidade OPACA e ESTÁVEL do plano ao longo de todas as suas versões (drift produz
-- uma versão nova do MESMO plan_id, nunca um plan_id novo). A validação de que readiness_outcome corresponde
-- a ReadyForCanary acontece inteiramente no Domain (CanaryPlan.Compose) ANTES de persistir — esta tabela não
-- reimplementa essa regra.
CREATE TABLE dbo.canary_plans
(
    tenant_id                     UNIQUEIDENTIFIER NOT NULL,
    project_id                    UNIQUEIDENTIFIER NOT NULL,
    plan_version                  INT              NOT NULL,
    plan_id                       UNIQUEIDENTIFIER NOT NULL,
    readiness_review_version      INT              NOT NULL,
    readiness_review_fingerprint  CHAR(64)         NOT NULL,
    build_commit_sha              CHAR(40)         NOT NULL,
    build_artifact_digest         CHAR(64)         NOT NULL,
    policy_version_fingerprint    CHAR(64)         NOT NULL,
    capability_matrix_fingerprint CHAR(64)         NOT NULL,
    plan_fingerprint              CHAR(64)         NOT NULL,
    authorized_by                 NVARCHAR(200)    NOT NULL,
    authorized_by_role            NVARCHAR(50)     NOT NULL,
    correlation_id                UNIQUEIDENTIFIER NOT NULL,
    authorized_at_utc             DATETIME2(3)     NOT NULL,
    schema_version                NVARCHAR(100)    NOT NULL,
    plan_hash                     CHAR(64)         NOT NULL,
    CONSTRAINT PK_canary_plans PRIMARY KEY (tenant_id, project_id, plan_version),
    CONSTRAINT FK_canary_plans_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_canary_plans_version CHECK (plan_version >= 1),
    CONSTRAINT CK_canary_plans_readiness_version CHECK (readiness_review_version >= 1),
    CONSTRAINT CK_canary_plans_commit_sha_length CHECK (LEN(build_commit_sha) = 40)
);
GO

CREATE INDEX IX_canary_plans_scope ON dbo.canary_plans (tenant_id, project_id, plan_version DESC);
GO

-- Consulta por identidade opaca do plano (ex.: relatórios que precisam de "todas as versões deste MESMO
-- plano", distinto de "todas as versões deste tenant/projeto" — hoje coincidentes, mas a identidade
-- separada preserva a modelagem correta caso um plano futuro seja explicitamente descontinuado/recriado).
CREATE INDEX IX_canary_plans_plan_id ON dbo.canary_plans (tenant_id, project_id, plan_id);
GO

GRANT SELECT, INSERT ON dbo.canary_plans TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.canary_plans,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.canary_plans AFTER INSERT;
GO

-- 2) Resultado individual de CADA cenário do catálogo dentro de UMA versão do plano (escopo obrigatório item
-- 6) — cada submissão (atestação de operador, resolução SystemDerived, ou decisão de aprovação) é uma NOVA
-- linha append-only (result_version), nunca uma atualização; permite reidratar/revalidar
-- CanaryScenarioResult.ComputeContentFingerprint/ComputeRecordHash a partir das linhas REALMENTE
-- persistidas (fronteira não confiável, defesa contra adulteração isolada de qualquer coluna). Escopada
-- explicitamente a plan_version (nunca "o plano mais recente" implicitamente) — submissões contra uma
-- versão do plano que já não é a vigente são recusadas pela Application layer ANTES de qualquer INSERT
-- (CanaryPlanSupersededException, escopo obrigatório item 5).
CREATE TABLE dbo.canary_scenario_results
(
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    plan_version         INT              NOT NULL,
    scenario_id          NVARCHAR(80)     NOT NULL,
    result_version       INT              NOT NULL,
    status               TINYINT          NOT NULL,
    evidence_kind        TINYINT          NOT NULL,
    evidence_fingerprint CHAR(64)         NOT NULL,
    evidence_locator     NVARCHAR(300)    NOT NULL,
    reason_code          NVARCHAR(200)    NOT NULL,
    observed_at_utc      DATETIME2(3)     NOT NULL,
    submitted_by         NVARCHAR(200)    NOT NULL,
    submitted_by_role    NVARCHAR(50)     NOT NULL,
    correlation_id       UNIQUEIDENTIFIER NOT NULL,
    recorded_at_utc      DATETIME2(3)     NOT NULL,
    schema_version       NVARCHAR(100)    NOT NULL,
    content_fingerprint  CHAR(64)         NOT NULL,
    record_hash          CHAR(64)         NOT NULL,
    CONSTRAINT PK_canary_scenario_results PRIMARY KEY (tenant_id, project_id, plan_version, scenario_id, result_version),
    CONSTRAINT FK_canary_scenario_results_plan FOREIGN KEY (tenant_id, project_id, plan_version)
        REFERENCES dbo.canary_plans (tenant_id, project_id, plan_version),
    CONSTRAINT CK_canary_scenario_results_status CHECK (status BETWEEN 0 AND 5),
    CONSTRAINT CK_canary_scenario_results_evidence_kind CHECK (evidence_kind BETWEEN 0 AND 3),
    -- AB-I8-009: esta tabela é o análogo estrutural de dbo.production_readiness_review_control_results/0042
    -- (resultados RESOLVIDOS de um cenário — Pending/NotPerformed/Blocked/Fail/Pass —, nunca uma tabela de
    -- atestações puras como dbo.production_readiness_control_attestations/0042). CanaryEvidenceReference.None
    -- é o valor fail-closed INTENCIONAL para NotPerformed/Blocked quando nenhuma evidência canônica existe
    -- ainda (CanaryScenarioEvidenceResolvers, CanaryEvidenceReference.None: "default fail-closed quando um
    -- cenário nunca foi observado") — um CHECK que proibisse evidence_kind=None para qualquer status
    -- rejeitaria esse resultado legítimo. Por isso, ao contrário de CK_prca_evidence_kind_never_none/0042
    -- (que se aplica a uma tabela só-de-atestação), aqui — como em CK_prrcr_pass_requires_evidence/0042 —
    -- só Pass (5) exige evidência real (evidence_kind <> None/0); mesmo um INSERT direto/adulterado fora da
    -- store nunca conseguiria persistir um resultado Pass sem evidência.
    CONSTRAINT CK_canary_scenario_results_pass_requires_evidence CHECK (status <> 5 OR evidence_kind <> 0),
    CONSTRAINT CK_canary_scenario_results_version CHECK (result_version >= 1)
);
GO

CREATE INDEX IX_canary_scenario_results_scope ON dbo.canary_scenario_results
    (tenant_id, project_id, plan_version, scenario_id, result_version DESC);
GO

GRANT SELECT, INSERT ON dbo.canary_scenario_results TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.canary_scenario_results,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.canary_scenario_results AFTER INSERT;
GO
