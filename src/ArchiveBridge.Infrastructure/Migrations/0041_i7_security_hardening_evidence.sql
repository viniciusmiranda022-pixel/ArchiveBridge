-- I7 Hardening — Passo 4 (AB-I7-008): security hardening, supply-chain e production security evidence.
-- Materializa cinco linhas de evidência IMUTÁVEIS e append-only, tenant/project-scoped, tamper-evident
-- (mesmo padrão de 0038/0040): worker hardening baseline, WDAC/App Control policy evidence, supply-chain
-- build provenance, incident-response drills sintéticos e o pen-test readiness bundle. Nenhuma linha
-- destas tabelas é jamais atualizada ou apagada; nenhuma delas aplica NENHUM controle a nenhum host real
-- (STOP-THE-LINE do work order: sem WDAC/GPO/Defender/Intune/Azure Policy aplicados em produção aqui).
--   control (Domain.Security.WorkerHardeningControl): OsPatchingSupported=0,
--     DefenderForEndpointTamperProtection=1, AppControlWdacAllowlist=2, SecureBootVTpmCredentialGuard=3,
--     BitLocker=4, SmbV1AndLegacyTlsDisabled=5, RdpDenyByDefault=6, ServiceIdentityLeastPrivilege=7,
--     ScratchAclNoExecute=8, OutboundRestricted=9, CrashDumpHandling=10, MdeTenantPolicyEnforcement=11
--   status (Domain.Security.WorkerHardeningStatus): NotMeasured=0, Blocked=1, Pass=2
--   drill_type (Domain.Security.IncidentResponseDrillType): SecretLeakCanary=0, HashMismatchTampering=1,
--     CrossTenantDenial=2
--   outcome (Domain.Security.IncidentResponseDrillOutcome): Contained=0, Failed=1
--   pentest status (Domain.Security.PenTestReadinessStatus): NotPerformed=0, Blocked=1 — a COLUNA em si só
--     aceita 0/1: é estruturalmente impossível persistir um valor "Pass"/concluído (acceptance criteria 7).
--
-- Aditiva, append-only e não destrutiva: cria CINCO tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0040 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos. Simplificação deliberada frente ao padrão de 0038/0040 (registrada em
-- docs/engineering/security-hardening-evidence-matrix.md): nenhuma destas cinco tabelas tem uma tabela
-- companheira "*_audit_events" própria — cada linha já é, por si só, o evento auditável append-only
-- (mesmo texto/timestamp/ator/correlação de uma trilha dedicada); reduzir para uma tabela por área mantém
-- este Passo no escopo de evidência/domínio/persistência pedido, sem abrir 10 tabelas nesta única migration.

-- 1) Worker hardening baseline (item 1) — cada linha é a verificação de UM controle em UMA versão.
CREATE TABLE dbo.security_worker_hardening_evidence
(
    tenant_id                    UNIQUEIDENTIFIER NOT NULL,
    project_id                   UNIQUEIDENTIFIER NOT NULL,
    control                      TINYINT          NOT NULL,
    control_version              INT              NOT NULL,
    status                       TINYINT          NOT NULL,
    measurement_measured_at_utc  DATETIME2(3)     NULL,
    measurement_method           NVARCHAR(200)    NULL,
    evidence_fingerprint         CHAR(64)         NOT NULL,
    blocked_reason               NVARCHAR(1000)   NOT NULL,
    notes                        NVARCHAR(1000)   NOT NULL,
    content_fingerprint          CHAR(64)         NOT NULL,
    executed_by                  NVARCHAR(200)    NOT NULL,
    executed_by_role             NVARCHAR(50)     NOT NULL,
    correlation_id               UNIQUEIDENTIFIER NOT NULL,
    executed_at_utc              DATETIME2(3)     NOT NULL,
    schema_version                NVARCHAR(100)    NOT NULL,
    record_hash                  CHAR(64)         NOT NULL,
    CONSTRAINT PK_security_worker_hardening_evidence PRIMARY KEY (tenant_id, project_id, control, control_version),
    CONSTRAINT FK_whe_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_whe_control CHECK (control BETWEEN 0 AND 11),
    CONSTRAINT CK_whe_status CHECK (status BETWEEN 0 AND 2),
    CONSTRAINT CK_whe_control_version CHECK (control_version >= 1),
    -- Item 1/STOP-THE-LINE: MdeTenantPolicyEnforcement (11) é Unsupported nesta baseline on-premises —
    -- nunca pode ser Pass (2), bloqueado também no domínio (WorkerHardeningControlRecord.Pass); este CHECK
    -- é defesa em profundidade ao nível do schema (mesmo padrão de CK_rre_ha_never_pass em 0040).
    CONSTRAINT CK_whe_mde_never_pass CHECK (NOT (control = 11 AND status = 2)),
    CONSTRAINT CK_whe_pass_requires_measurement
        CHECK (status <> 2 OR (measurement_measured_at_utc IS NOT NULL AND measurement_method IS NOT NULL)),
    CONSTRAINT CK_whe_not_measured_has_no_measurement
        CHECK (status <> 0 OR (measurement_measured_at_utc IS NULL AND measurement_method IS NULL)),
    CONSTRAINT CK_whe_measurement_pair
        CHECK ((measurement_measured_at_utc IS NULL AND measurement_method IS NULL)
            OR (measurement_measured_at_utc IS NOT NULL AND measurement_method IS NOT NULL))
);
GO

CREATE INDEX IX_whe_scope ON dbo.security_worker_hardening_evidence
    (tenant_id, project_id, control, control_version DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhum registro é jamais atualizado ou apagado.
GRANT SELECT, INSERT ON dbo.security_worker_hardening_evidence TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_worker_hardening_evidence,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_worker_hardening_evidence AFTER INSERT;
GO

-- 2) WDAC/App Control policy evidence (item 2) — entradas canônicas em UMA coluna (nunca allow-all: a
-- validação de que cada entrada é hash e/ou publisher+path-rule específica acontece no Domain
-- ANTES de persistir; policy_digest cobre o conteúdo de TODAS as entradas para detecção de tampering).
CREATE TABLE dbo.security_wdac_policy_evidence
(
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    policy_version        INT              NOT NULL,
    entries_canonical     NVARCHAR(MAX)    NOT NULL,
    policy_digest         CHAR(64)         NOT NULL,
    content_fingerprint   CHAR(64)         NOT NULL,
    issued_by             NVARCHAR(200)    NOT NULL,
    issued_by_role        NVARCHAR(50)     NOT NULL,
    correlation_id        UNIQUEIDENTIFIER NOT NULL,
    issued_at_utc         DATETIME2(3)     NOT NULL,
    schema_version         NVARCHAR(100)    NOT NULL,
    record_hash           CHAR(64)         NOT NULL,
    CONSTRAINT PK_security_wdac_policy_evidence PRIMARY KEY (tenant_id, project_id, policy_version),
    CONSTRAINT FK_wpe_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_wpe_policy_version CHECK (policy_version >= 1)
);
GO

CREATE INDEX IX_wpe_scope ON dbo.security_wdac_policy_evidence (tenant_id, project_id, policy_version DESC);
GO

GRANT SELECT, INSERT ON dbo.security_wdac_policy_evidence TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_wdac_policy_evidence,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_wdac_policy_evidence AFTER INSERT;
GO

-- 3) Supply-chain build provenance (item 3) — identidade determinística por artifact/commit/digest;
-- drift entre build aprovada e artifact promovido é decidido em memória por
-- Domain.Security.ArtifactPromotionVerifier (nunca no banco) e falha fechado (exceção), nunca silencioso.
CREATE TABLE dbo.security_build_provenance
(
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    artifact_name         NVARCHAR(200)    NOT NULL,
    artifact_version      INT              NOT NULL,
    source_commit_sha     CHAR(40)         NOT NULL,
    builder_identity      NVARCHAR(200)    NOT NULL,
    build_timestamp_utc   DATETIME2(3)     NOT NULL,
    artifact_digest       CHAR(64)         NOT NULL,
    content_fingerprint   CHAR(64)         NOT NULL,
    approved_by           NVARCHAR(200)    NOT NULL,
    approved_by_role      NVARCHAR(50)     NOT NULL,
    correlation_id        UNIQUEIDENTIFIER NOT NULL,
    approved_at_utc       DATETIME2(3)     NOT NULL,
    schema_version         NVARCHAR(100)    NOT NULL,
    record_hash           CHAR(64)         NOT NULL,
    CONSTRAINT PK_security_build_provenance PRIMARY KEY (tenant_id, project_id, artifact_name, artifact_version),
    CONSTRAINT FK_sbp_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_sbp_artifact_version CHECK (artifact_version >= 1),
    CONSTRAINT CK_sbp_commit_sha_length CHECK (LEN(source_commit_sha) = 40)
);
GO

CREATE INDEX IX_sbp_scope ON dbo.security_build_provenance
    (tenant_id, project_id, artifact_name, artifact_version DESC);
GO

GRANT SELECT, INSERT ON dbo.security_build_provenance TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_build_provenance,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_build_provenance AFTER INSERT;
GO

-- 4) Incident-response drills sintéticos e não destrutivos (item 5) — nunca segredo/PII (apenas digest da
-- evidência); disposition é validada fail-closed contra aparência de segredo/PII no Domain ANTES de
-- persistir (EvidenceText/SecretRedactor.ContainsSuspectedSecret).
CREATE TABLE dbo.security_incident_response_drills
(
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    drill_type            TINYINT          NOT NULL,
    drill_version         INT              NOT NULL,
    outcome               TINYINT          NOT NULL,
    started_at_utc        DATETIME2(3)     NOT NULL,
    completed_at_utc      DATETIME2(3)     NOT NULL,
    evidence_digest       CHAR(64)         NOT NULL,
    disposition           NVARCHAR(1000)   NOT NULL,
    content_fingerprint   CHAR(64)         NOT NULL,
    executed_by           NVARCHAR(200)    NOT NULL,
    executed_by_role      NVARCHAR(50)     NOT NULL,
    correlation_id        UNIQUEIDENTIFIER NOT NULL,
    recorded_at_utc       DATETIME2(3)     NOT NULL,
    schema_version         NVARCHAR(100)    NOT NULL,
    record_hash           CHAR(64)         NOT NULL,
    CONSTRAINT PK_security_incident_response_drills PRIMARY KEY (tenant_id, project_id, drill_type, drill_version),
    CONSTRAINT FK_ird_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_ird_drill_type CHECK (drill_type BETWEEN 0 AND 2),
    CONSTRAINT CK_ird_outcome CHECK (outcome BETWEEN 0 AND 1),
    CONSTRAINT CK_ird_drill_version CHECK (drill_version >= 1),
    CONSTRAINT CK_ird_timestamps CHECK (completed_at_utc >= started_at_utc)
);
GO

CREATE INDEX IX_ird_scope ON dbo.security_incident_response_drills
    (tenant_id, project_id, drill_type, drill_version DESC);
GO

GRANT SELECT, INSERT ON dbo.security_incident_response_drills TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_incident_response_drills,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_incident_response_drills AFTER INSERT;
GO

-- 5) Pen-test readiness bundle (item 6/acceptance criteria 7) — a COLUNA status em si só aceita 0/1
-- (NotPerformed/Blocked): é estruturalmente impossível, mesmo por INSERT direto/adulterado fora da
-- aplicação (privilege spoofing), persistir um valor que representasse "pen-test concluído/Pass" — o tipo
-- Domain.Security.PenTestReadinessStatus nem sequer POSSUI esse caso, e este CHECK é a defesa em
-- profundidade equivalente ao nível do schema (mesmo padrão de CK_rre_ha_never_pass/CK_whe_mde_never_pass).
CREATE TABLE dbo.security_pentest_readiness_bundles
(
    tenant_id                        UNIQUEIDENTIFIER NOT NULL,
    project_id                       UNIQUEIDENTIFIER NOT NULL,
    bundle_version                   INT              NOT NULL,
    status                           TINYINT          NOT NULL,
    scope_summary                    NVARCHAR(2000)   NOT NULL,
    attack_surface_summary           NVARCHAR(2000)   NOT NULL,
    trust_boundaries_summary         NVARCHAR(2000)   NOT NULL,
    synthetic_fixtures_description   NVARCHAR(2000)   NOT NULL,
    known_blocked_items_summary      NVARCHAR(2000)   NOT NULL,
    target_build_digest              CHAR(64)         NOT NULL,
    blocked_reason                   NVARCHAR(1000)   NOT NULL,
    content_fingerprint              CHAR(64)         NOT NULL,
    prepared_by                      NVARCHAR(200)    NOT NULL,
    prepared_by_role                 NVARCHAR(50)     NOT NULL,
    correlation_id                   UNIQUEIDENTIFIER NOT NULL,
    prepared_at_utc                  DATETIME2(3)     NOT NULL,
    schema_version                    NVARCHAR(100)    NOT NULL,
    record_hash                      CHAR(64)         NOT NULL,
    CONSTRAINT PK_security_pentest_readiness_bundles PRIMARY KEY (tenant_id, project_id, bundle_version),
    CONSTRAINT FK_prb_project FOREIGN KEY (tenant_id, project_id) REFERENCES dbo.projects (tenant_id, project_id),
    -- Estruturalmente só 0 (NotPerformed) ou 1 (Blocked): nenhum valor "Pass"/concluído é armazenável.
    CONSTRAINT CK_prb_status_never_pass CHECK (status BETWEEN 0 AND 1),
    CONSTRAINT CK_prb_bundle_version CHECK (bundle_version >= 1)
);
GO

CREATE INDEX IX_prb_scope ON dbo.security_pentest_readiness_bundles (tenant_id, project_id, bundle_version DESC);
GO

GRANT SELECT, INSERT ON dbo.security_pentest_readiness_bundles TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_pentest_readiness_bundles,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.security_pentest_readiness_bundles AFTER INSERT;
GO
