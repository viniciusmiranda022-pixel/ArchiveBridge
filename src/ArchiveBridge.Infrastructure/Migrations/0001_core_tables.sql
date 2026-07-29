-- Vertical Slice 1 — Núcleo durável de Jobs (SQL Server on-premises).
-- Enums são persistidos como TINYINT com o MESMO valor numérico das enums do domínio:
--   JobState:  Pending=0, Processing=1, RetryScheduled=2, Completed=3, Failed=4, Cancelled=5
--   Workload:  Control=0, EnterpriseVault=1, Pst=2, Upload=3, Reconciliation=4, Evidence=5
--   ReasonCode: Created=0, Claimed=1, Completed=2, RetryScheduled=3, Failed=4, Cancelled=5,
--               LeaseExpiredRecovered=6, AttemptsExhausted=7
-- IMPORTANTE: lease_epoch é o token de fencing; row_version (ROWVERSION) é concorrência otimista,
-- NÃO fencing. Auditoria não registra conteúdo sensível (apenas ids/códigos).

CREATE TABLE dbo.projects
(
    project_id     UNIQUEIDENTIFIER NOT NULL,
    tenant_id      UNIQUEIDENTIFIER NOT NULL,
    created_at_utc DATETIME2(3)     NOT NULL CONSTRAINT DF_projects_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_projects PRIMARY KEY (project_id),
    CONSTRAINT UQ_projects_tenant UNIQUE (tenant_id, project_id)
);

CREATE TABLE dbo.jobs
(
    job_id               UNIQUEIDENTIFIER NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    workload             TINYINT          NOT NULL,
    state                TINYINT          NOT NULL,
    priority             INT              NOT NULL CONSTRAINT DF_jobs_priority DEFAULT 0,
    attempt_count        INT              NOT NULL CONSTRAINT DF_jobs_attempts DEFAULT 0,
    owner_worker         NVARCHAR(200)    NULL,
    lease_epoch          BIGINT           NOT NULL CONSTRAINT DF_jobs_epoch DEFAULT 0,
    lease_expires_at_utc DATETIME2(3)     NULL,
    next_attempt_at_utc  DATETIME2(3)     NULL,
    last_error_code      TINYINT          NULL,
    created_at_utc       DATETIME2(3)     NOT NULL CONSTRAINT DF_jobs_created DEFAULT SYSUTCDATETIME(),
    updated_at_utc       DATETIME2(3)     NOT NULL CONSTRAINT DF_jobs_updated DEFAULT SYSUTCDATETIME(),
    row_version          ROWVERSION       NOT NULL,
    CONSTRAINT PK_jobs PRIMARY KEY (job_id),
    CONSTRAINT FK_jobs_project FOREIGN KEY (project_id) REFERENCES dbo.projects (project_id),
    CONSTRAINT CK_jobs_state CHECK (state BETWEEN 0 AND 5),
    CONSTRAINT CK_jobs_workload CHECK (workload BETWEEN 0 AND 5)
);

-- Índice de claim (anti-starvation): filtra por tenant/workload/estado e ordena por
-- prioridade desc., próxima tentativa e criação. tenant_id encabeça a chave (isolamento).
CREATE INDEX IX_jobs_claim
    ON dbo.jobs (tenant_id, workload, state, next_attempt_at_utc, created_at_utc)
    INCLUDE (project_id, priority, owner_worker, lease_epoch);

-- Índice do reaper: Jobs em Processing (state=1) ordenados por expiração do lease.
CREATE INDEX IX_jobs_lease_expiry
    ON dbo.jobs (lease_expires_at_utc)
    INCLUDE (tenant_id, project_id, attempt_count, lease_epoch, owner_worker)
    WHERE state = 1;

CREATE TABLE dbo.job_attempts
(
    attempt_id     BIGINT IDENTITY (1,1) NOT NULL,
    job_id         UNIQUEIDENTIFIER      NOT NULL,
    tenant_id      UNIQUEIDENTIFIER      NOT NULL,
    project_id     UNIQUEIDENTIFIER      NOT NULL,
    attempt_number INT                   NOT NULL,
    owner_worker   NVARCHAR(200)         NOT NULL,
    lease_epoch    BIGINT                NOT NULL,
    started_at_utc DATETIME2(3)          NOT NULL CONSTRAINT DF_job_attempts_started DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_job_attempts PRIMARY KEY (attempt_id),
    CONSTRAINT FK_job_attempts_job FOREIGN KEY (job_id) REFERENCES dbo.jobs (job_id)
);

CREATE INDEX IX_job_attempts_job
    ON dbo.job_attempts (tenant_id, job_id, attempt_number);

CREATE TABLE dbo.job_state_transitions
(
    transition_id   BIGINT IDENTITY (1,1) NOT NULL,
    job_id          UNIQUEIDENTIFIER      NOT NULL,
    tenant_id       UNIQUEIDENTIFIER      NOT NULL,
    project_id      UNIQUEIDENTIFIER      NOT NULL,
    from_state      TINYINT               NULL,
    to_state        TINYINT               NOT NULL,
    reason_code     TINYINT               NOT NULL,
    lease_epoch     BIGINT                NOT NULL,
    worker_id       NVARCHAR(200)         NULL,
    correlation_id  UNIQUEIDENTIFIER      NOT NULL,
    occurred_at_utc DATETIME2(3)          NOT NULL CONSTRAINT DF_jst_occurred DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_job_state_transitions PRIMARY KEY (transition_id),
    CONSTRAINT FK_jst_job FOREIGN KEY (job_id) REFERENCES dbo.jobs (job_id)
);

CREATE INDEX IX_jst_job
    ON dbo.job_state_transitions (tenant_id, job_id, transition_id);
