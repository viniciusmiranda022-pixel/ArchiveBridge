-- I5/EPIC-06 Passo 3 — AB-I5-009: fundação segura e reiniciável de upload Purview Network Upload via
-- AzCopy, consumindo exclusivamente o SAS custodiado no Passo 2 (dbo.purview_sas_upload_handles) e os PST
-- parts canônicos vinculados pela ponte AB-I5-010 (dbo.wave_partition_output_bindings, migration 0028).
-- Migração ADITIVA: cria DUAS tabelas novas, sem alterar nenhuma migration anterior.
--   dbo.purview_upload_requests — o pedido lógico DURÁVEL de upload de UMA wave (item 8): um único pedido
--     por (tenant, projeto, wave), para sempre — vinculado 1:1 ao Job durável (workload Upload=3) que
--     reivindica/executa/reintenta o transporte (mesmo padrão de dbo.ev_export_requests, AB-4C-005).
--   dbo.purview_upload_attempts — a história append-only de tentativas (items 8/10/11/14): cada linha é
--     UMA tentativa imutável, com a identidade lógica (item 14) calculada NAQUELE instante e, somente
--     quando outcome=Uploaded (transporte comprovado), a evidência SANITIZADA do AzCopy (binário/contadores/
--     prefixo remoto) — NUNCA stdout/stderr bruto, NUNCA o SAS, NUNCA caminho físico absoluto.
--   outcome (PurviewUploadAttemptOutcome): Uploaded=0, SourceIntegrityFailed=1, BinaryMismatch=2,
--     SasDenied=3, ProcessFailed=4 (valores fixados explicitamente no enum C#).

CREATE TABLE dbo.purview_upload_requests
(
    request_id      UNIQUEIDENTIFIER NOT NULL,
    job_id          UNIQUEIDENTIFIER NOT NULL,
    tenant_id       UNIQUEIDENTIFIER NOT NULL,
    project_id      UNIQUEIDENTIFIER NOT NULL,
    wave_id         UNIQUEIDENTIFIER NOT NULL,
    correlation_id  UNIQUEIDENTIFIER NOT NULL,
    created_at_utc  DATETIME2(3)     NOT NULL,
    request_hash    CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_upload_requests PRIMARY KEY (request_id),
    CONSTRAINT UQ_purview_upload_requests_scope UNIQUE (request_id, tenant_id, project_id),
    CONSTRAINT UQ_purview_upload_requests_job UNIQUE (job_id),
    -- Item 8/14: um único pedido lógico por wave, para sempre — o backstop de idempotência do enfileiramento.
    CONSTRAINT UQ_purview_upload_requests_wave UNIQUE (tenant_id, project_id, wave_id),
    -- A wave referenciada é obrigatoriamente do MESMO tenant/projeto (anti cross-scope) e precisa existir.
    CONSTRAINT FK_purview_upload_requests_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id)
);
GO

GRANT SELECT, INSERT ON dbo.purview_upload_requests TO ab_app_role;
GO
-- A identidade de MANUTENÇÃO precisa enxergar esta tabela: um futuro leitor de escopos pendentes (mesmo
-- padrão de SqlEvExportPendingScopeReader) faz EXISTS(...) sobre dbo.purview_upload_requests para enumerar
-- escopos elegíveis entre tenants — sem este GRANT, a leitura de manutenção falha fechado por permissão negada.
GRANT SELECT ON dbo.purview_upload_requests TO ab_maintenance_role;
GO

CREATE TABLE dbo.purview_upload_attempts
(
    attempt_id            UNIQUEIDENTIFIER NOT NULL,
    request_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    attempt_number        INT              NOT NULL,
    identity_hash         CHAR(64)         NOT NULL,
    outcome               TINYINT          NOT NULL,
    blocking_reason       NVARCHAR(200)    NULL,
    process_exit_code     INT              NULL,
    -- Evidência SANITIZADA do transporte (item 10) — só preenchida quando outcome=Uploaded (CK abaixo).
    binary_version        NVARCHAR(50)     NULL,
    binary_sha256         CHAR(64)         NULL,
    expected_file_count   INT              NULL,
    expected_total_bytes  BIGINT           NULL,
    remote_wave_segment   NVARCHAR(200)    NULL,
    started_at_utc        DATETIME2(3)     NOT NULL,
    completed_at_utc      DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_purview_upload_attempts PRIMARY KEY (attempt_id),
    CONSTRAINT FK_purview_upload_attempts_request FOREIGN KEY (request_id, tenant_id, project_id)
        REFERENCES dbo.purview_upload_requests (request_id, tenant_id, project_id),
    CONSTRAINT UQ_purview_upload_attempts_number UNIQUE (tenant_id, project_id, request_id, attempt_number),
    CONSTRAINT CK_purview_upload_attempts_number CHECK (attempt_number >= 1),
    CONSTRAINT CK_purview_upload_attempts_outcome CHECK (outcome BETWEEN 0 AND 4),
    CONSTRAINT CK_purview_upload_attempts_timestamps CHECK (completed_at_utc >= started_at_utc),
    -- Item 11/13: evidência de transporte só existe quando o desfecho é Uploaded (UploadVerified) — nenhum
    -- outro desfecho pode carregar campos que sugiram sucesso parcial. Reforçado também no Domain (defesa
    -- em profundidade) por PurviewUploadEvidence, que só é construída no caminho de sucesso.
    CONSTRAINT CK_purview_upload_attempts_evidence_only_when_uploaded CHECK (
        (outcome = 0 AND binary_version IS NOT NULL AND binary_sha256 IS NOT NULL
            AND expected_file_count IS NOT NULL AND expected_total_bytes IS NOT NULL AND remote_wave_segment IS NOT NULL)
        OR
        (outcome <> 0 AND binary_version IS NULL AND binary_sha256 IS NULL
            AND expected_file_count IS NULL AND expected_total_bytes IS NULL AND remote_wave_segment IS NULL)
    )
);
GO

-- Índice de leitura do réplay idempotente (item 14): a tentativa mais recente de um pedido, mais rápido.
CREATE INDEX IX_purview_upload_attempts_latest
    ON dbo.purview_upload_attempts (tenant_id, project_id, request_id, attempt_number DESC);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE. Manutenção NÃO recebe grant algum
-- (evidência estrutural, sem necessidade operacional de leitura pela identidade de manutenção).
GRANT SELECT, INSERT ON dbo.purview_upload_attempts TO ab_app_role;
GO

-- Isolamento por tenant (RLS) nas duas tabelas novas. Isolamento POR PROJETO é reforçado pelo filtro
-- explícito por project_id em toda query (Contracts/Infrastructure) e pelas UNIQUE compostas.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_upload_requests,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_upload_requests AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_upload_attempts,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_upload_attempts AFTER INSERT;
GO
