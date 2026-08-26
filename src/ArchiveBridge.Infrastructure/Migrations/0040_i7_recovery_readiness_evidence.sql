-- I7 Hardening — Passo 3 (AB-I7-005): recovery readiness / DR evidence, tenant/project-scoped,
-- append-only e tamper-evident. Materializa cada exercício de restore drill, pending-work rebuild,
-- artifact/evidence recovery e avaliação de HA/failover como uma linha imutável — nunca sobrescrita,
-- nunca reinterpretada por status/configuração declarativa (item 9: "nenhuma conclusão de HA baseada
-- apenas em documentação ou configuração declarativa").
--   exercise_type (Domain.Recovery.RecoveryExerciseType): RestoreDrill=0, PendingWorkRebuild=1,
--     ArtifactEvidenceRecovery=2, HaFailover=3
--   status (Domain.Recovery.RecoveryReadinessStatus): NotMeasured=0, Blocked=1, Pass=2
--   objective (Domain.Recovery.RecoveryObjective): None=0, ControlPlaneRto=1, ControlPlaneRpo=2,
--     EvidenceLogicalRpo=3
--
-- Aditiva, append-only e não destrutiva: cria DUAS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0039 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos.

-- Append-only, versionado por (tenant, project, tipo de exercício): a MESMA impressão digital de
-- resultado (exercise_fingerprint) converge para a MESMA exercise_version (replay idempotente); um
-- resultado REALMENTE diferente produz uma versão nova — nunca sobrescreve uma anterior. record_hash
-- cobre TODOS os campos persistidos (tamper-evident, mesmo princípio de certificate_hash em 0038).
CREATE TABLE dbo.recovery_readiness_evidence
(
    tenant_id                       UNIQUEIDENTIFIER NOT NULL,
    project_id                      UNIQUEIDENTIFIER NOT NULL,
    exercise_type                   TINYINT          NOT NULL,
    exercise_version                INT              NOT NULL,
    status                          TINYINT          NOT NULL,
    objective                       TINYINT          NOT NULL,
    objective_threshold_ticks       BIGINT           NULL,
    measurement_started_at_utc      DATETIME2(3)     NULL,
    measurement_completed_at_utc    DATETIME2(3)     NULL,
    evidence_fingerprint            CHAR(64)         NOT NULL,
    failure_domain                  NVARCHAR(1000)   NOT NULL,
    notes                           NVARCHAR(1000)   NOT NULL,
    exercise_fingerprint            CHAR(64)         NOT NULL,
    executed_by                     NVARCHAR(200)    NOT NULL,
    executed_by_role                NVARCHAR(50)     NOT NULL,
    correlation_id                  UNIQUEIDENTIFIER NOT NULL,
    executed_at_utc                 DATETIME2(3)     NOT NULL,
    schema_version                  NVARCHAR(100)    NOT NULL,
    record_hash                     CHAR(64)         NOT NULL,
    CONSTRAINT PK_recovery_readiness_evidence
        PRIMARY KEY (tenant_id, project_id, exercise_type, exercise_version),
    CONSTRAINT FK_rre_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_rre_exercise_type CHECK (exercise_type BETWEEN 0 AND 3),
    CONSTRAINT CK_rre_status CHECK (status BETWEEN 0 AND 2),
    CONSTRAINT CK_rre_objective CHECK (objective BETWEEN 0 AND 3),
    CONSTRAINT CK_rre_exercise_version CHECK (exercise_version >= 1),
    -- Item 9/STOP-THE-LINE: HaFailover (3) nunca pode ser Pass (2) — bloqueado também no domínio
    -- (RecoveryReadinessRecord.Pass), este CHECK é defesa em profundidade ao nível do schema.
    CONSTRAINT CK_rre_ha_never_pass CHECK (NOT (exercise_type = 3 AND status = 2)),
    -- Pass (2) exige medição real (início/fim ambos presentes); NotMeasured (0) nunca carrega medição.
    CONSTRAINT CK_rre_pass_requires_measurement
        CHECK (status <> 2 OR (measurement_started_at_utc IS NOT NULL AND measurement_completed_at_utc IS NOT NULL)),
    CONSTRAINT CK_rre_not_measured_has_no_measurement
        CHECK (status <> 0 OR (measurement_started_at_utc IS NULL AND measurement_completed_at_utc IS NULL)),
    CONSTRAINT CK_rre_measurement_pair
        CHECK ((measurement_started_at_utc IS NULL AND measurement_completed_at_utc IS NULL)
            OR (measurement_started_at_utc IS NOT NULL AND measurement_completed_at_utc IS NOT NULL
                AND measurement_completed_at_utc >= measurement_started_at_utc))
);
GO

-- Resolução do registro VIGENTE (GetLatestAsync/GetHistoryAsync) e do lock de convergência/concorrência
-- de RecordExerciseAsync (WITH UPDLOCK, HOLDLOCK sobre este mesmo índice).
CREATE INDEX IX_rre_scope ON dbo.recovery_readiness_evidence
    (tenant_id, project_id, exercise_type, exercise_version DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhum registro é jamais atualizado ou apagado.
GRANT SELECT, INSERT ON dbo.recovery_readiness_evidence TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.recovery_readiness_evidence,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.recovery_readiness_evidence AFTER INSERT;
GO

-- Trilha auditável append-only de eventos sobre um registro de readiness (emissão/replay/verificação) —
-- mesmo padrão de purview_reconciliation_certificate_audit_events (0038). Autocontida (sem FK ao
-- registro) para permanecer sempre insertável mesmo quando o evento é uma falha de integridade.
CREATE TABLE dbo.recovery_readiness_audit_events
(
    event_id           BIGINT IDENTITY(1,1) NOT NULL,
    tenant_id           UNIQUEIDENTIFIER NOT NULL,
    project_id          UNIQUEIDENTIFIER NOT NULL,
    exercise_type       TINYINT          NOT NULL,
    exercise_version    INT              NULL,
    event_type          TINYINT          NOT NULL, -- Issued=0, Converged=1
    actor_id            NVARCHAR(200)    NOT NULL,
    actor_role          NVARCHAR(50)     NOT NULL,
    reason               NVARCHAR(500)    NOT NULL,
    correlation_id       UNIQUEIDENTIFIER NOT NULL,
    occurred_at_utc      DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_recovery_readiness_audit_events PRIMARY KEY (event_id),
    CONSTRAINT CK_rrae_exercise_type CHECK (exercise_type BETWEEN 0 AND 3),
    CONSTRAINT CK_rrae_event_type CHECK (event_type BETWEEN 0 AND 1)
);
GO

CREATE INDEX IX_rrae_scope ON dbo.recovery_readiness_audit_events
    (tenant_id, project_id, exercise_type, occurred_at_utc DESC);
GO

GRANT SELECT, INSERT ON dbo.recovery_readiness_audit_events TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.recovery_readiness_audit_events,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.recovery_readiness_audit_events AFTER INSERT;
GO
