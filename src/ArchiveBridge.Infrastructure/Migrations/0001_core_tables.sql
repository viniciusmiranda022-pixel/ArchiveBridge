-- Vertical Slice 1 — Núcleo durável de Jobs (SQL Server on-premises).
-- Enums são persistidos como TINYINT com o MESMO valor numérico das enums do domínio:
--   JobState:  Pending=0, Processing=1, RetryScheduled=2, Completed=3, Failed=4, Cancelled=5
--   Workload:  Control=0, EnterpriseVault=1, Pst=2, Upload=3, Reconciliation=4, Evidence=5
--   ReasonCode: Created=0, Claimed=1, Completed=2, RetryScheduled=3, Failed=4, Cancelled=5,
--               LeaseExpiredRecovered=6, AttemptsExhausted=7
-- IMPORTANTE: lease_epoch é o token de fencing; row_version (ROWVERSION) é concorrência otimista,
-- NÃO fencing. Auditoria não registra conteúdo sensível (apenas ids/códigos).
-- Coerência tenant/projeto é garantida no BANCO por chaves compostas (não apenas no código).

CREATE TABLE dbo.projects
(
    project_id     UNIQUEIDENTIFIER NOT NULL,
    tenant_id      UNIQUEIDENTIFIER NOT NULL,
    created_at_utc DATETIME2(3)     NOT NULL CONSTRAINT DF_projects_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_projects PRIMARY KEY (project_id),
    -- Alvo da FK composta de jobs: garante que o par (tenant, projeto) é único e coerente.
    CONSTRAINT UQ_projects_tenant_project UNIQUE (tenant_id, project_id)
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
    -- FK composta: um Job só existe para um par (tenant, projeto) coerente. Impede associar um Job
    -- a um projeto de OUTRO tenant (não haveria projeto (esteTenant, projeto)).
    CONSTRAINT FK_jobs_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.projects (tenant_id, project_id),
    -- Alvo das FKs compostas de job_attempts e job_state_transitions.
    CONSTRAINT UQ_jobs_identity UNIQUE (job_id, tenant_id, project_id),
    CONSTRAINT CK_jobs_state CHECK (state BETWEEN 0 AND 5),
    CONSTRAINT CK_jobs_workload CHECK (workload BETWEEN 0 AND 5),
    -- Intervalo permitido de JobPriority (equivalente à validação de domínio [0, 100]).
    CONSTRAINT CK_jobs_priority CHECK (priority BETWEEN 0 AND 100)
);

-- Índice de claim: filtra por tenant/projeto/workload/estado e ordena por próxima tentativa e
-- criação. tenant_id e project_id encabeçam a chave (isolamento por tenant E por projeto).
CREATE INDEX IX_jobs_claim
    ON dbo.jobs (tenant_id, project_id, workload, state, next_attempt_at_utc, created_at_utc)
    INCLUDE (priority, owner_worker, lease_epoch);

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
    CONSTRAINT FK_job_attempts_job FOREIGN KEY (job_id, tenant_id, project_id)
        REFERENCES dbo.jobs (job_id, tenant_id, project_id)
);

CREATE INDEX IX_job_attempts_job
    ON dbo.job_attempts (tenant_id, project_id, job_id, attempt_number);

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
    CONSTRAINT FK_jst_job FOREIGN KEY (job_id, tenant_id, project_id)
        REFERENCES dbo.jobs (job_id, tenant_id, project_id)
);

CREATE INDEX IX_jst_job
    ON dbo.job_state_transitions (tenant_id, project_id, job_id, transition_id);

-- Índice para a checagem de idempotência (transição já registrada por época + worker + estado).
CREATE INDEX IX_jst_idempotency
    ON dbo.job_state_transitions (job_id, lease_epoch, to_state, worker_id);
