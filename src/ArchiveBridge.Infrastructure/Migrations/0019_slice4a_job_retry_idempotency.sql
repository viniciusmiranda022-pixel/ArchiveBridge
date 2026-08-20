-- Slice 4A Passo 7 — idempotência do retry autorizado solicitado pelo Portal.
--
-- Aditiva e não destrutiva. Ajustada após revisão de engenharia (AB-7-002): a primeira versão desta
-- migration guardava apenas a chave de idempotência MAIS RECENTE em dbo.jobs.retry_idempotency_key, o que
-- permitia o cenário K1 aplicado -> K2 aplicado -> replay tardio de K1 ser tratado como uma NOVA aplicação
-- (a coluna já teria avançado para K2). Isso viola o contrato de idempotência: QUALQUER chave já consumida
-- deve permanecer replayável/conflitante para sempre, não apenas a última.
--
-- Esta versão substitui a coluna/índice em dbo.jobs por um livro-razão (ledger) append-only dedicado,
-- no MESMO padrão já aceito por dbo.mapping_validation_attempts (migration 0018): uma linha POR chave de
-- idempotência já consumida, permanentemente. O retry autorizado age sobre um Job JÁ EXISTENTE (não cria
-- um novo recurso de negócio) — por isso o ledger guarda apenas o vínculo chave -> job + o resultado
-- canônico necessário para responder um replay sem reaplicar/recalcular a transição, e não um comando
-- paralelo completo (nenhuma tabela de fila/comando duplicada é introduzida).
--
-- Backstop de concorrência: a chave primária (tenant_id, project_id, idempotency_key) é o único mecanismo
-- de deduplicação — duas solicitações concorrentes com a MESMA chave competem pela MESMA linha do ledger
-- (ver SqlJobStore.RequestManualRetryAsync, que lê essa linha com UPDLOCK+HOLDLOCK antes de decidir).
--
-- NÃO altera 0001–0018 nem qualquer objeto existente.

CREATE TABLE dbo.job_retry_requests
(
    tenant_id                    UNIQUEIDENTIFIER NOT NULL,
    project_id                   UNIQUEIDENTIFIER NOT NULL,
    idempotency_key              UNIQUEIDENTIFIER NOT NULL,
    job_id                       UNIQUEIDENTIFIER NOT NULL,
    applied_next_attempt_at_utc  DATETIME2(3)     NOT NULL,
    created_at_utc               DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_job_retry_requests PRIMARY KEY (tenant_id, project_id, idempotency_key),
    CONSTRAINT FK_job_retry_requests_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.projects (tenant_id, project_id),
    -- FK composta ao MESMO padrão de FK_jst_job (job_state_transitions): reforça fisicamente que o ledger
    -- nunca aponta para um Job inexistente ou de OUTRO tenant/projeto.
    CONSTRAINT FK_job_retry_requests_job FOREIGN KEY (job_id, tenant_id, project_id)
        REFERENCES dbo.jobs (job_id, tenant_id, project_id)
);
GO

-- Consulta operacional futura (histórico de retries por Job); não é o caminho de deduplicação (esse é a PK).
CREATE INDEX IX_job_retry_requests_job
    ON dbo.job_retry_requests (tenant_id, project_id, job_id, created_at_utc DESC);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE (mesmo padrão de 0015/0018).
GRANT SELECT, INSERT ON dbo.job_retry_requests TO ab_app_role;
GRANT SELECT ON dbo.job_retry_requests TO ab_maintenance_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.job_retry_requests,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.job_retry_requests AFTER INSERT;
GO
