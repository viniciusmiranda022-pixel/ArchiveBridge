-- I7/EPIC-07 Passo 2 — AB-I7-003: persistência append-only das execuções do BenchmarkHarness
-- (performance/capacity/SLO baseline). Materializa exatamente o que
-- Domain.Performance.PerformanceBenchmarkRunRecord exige: metadados de reprodutibilidade (build/runtime/
-- host profile/dataset sintético/seed/warmup/iterations) mais uma linha por medição de iteração — nunca
-- conteúdo real de PST/mailbox/e-mail (o Domain já recusa qualquer rótulo de dataset com aparência de
-- caminho/endereço antes de chegar aqui).
--   outcome (BenchmarkIterationOutcome): Success=0, Error=1, Cancelled=2, ResourceLimit=3
--
-- Aditiva, append-only e não destrutiva: cria DUAS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0038 — os arquivos das migrations anteriores permanecem byte-for-byte
-- intactos. Este Passo NÃO declara nenhum threshold de aprovação/regressão no banco — comparação é
-- responsabilidade de Application.Performance.PerformanceRegressionComparer, sempre informativa.

-- Uma linha por execução COMPLETA do harness (nunca uma tentativa parcial — a Application só persiste
-- depois que todas as iterações já rodaram). run_id é gerado pelo chamador (Guid.NewGuid, mesmo padrão de
-- PartitionExecutionId) — não há coluna de convergência/idempotência aqui: cada execução do harness é uma
-- nova evidência histórica, nunca substitui uma anterior.
CREATE TABLE dbo.performance_benchmark_runs
(
    run_id                  UNIQUEIDENTIFIER NOT NULL,
    tenant_id               UNIQUEIDENTIFIER NOT NULL,
    project_id              UNIQUEIDENTIFIER NOT NULL,
    scenario_name           NVARCHAR(200)    NOT NULL,
    build_version           NVARCHAR(200)    NOT NULL,
    runtime_description     NVARCHAR(200)    NOT NULL,
    host_profile            NVARCHAR(200)    NOT NULL,
    dataset_name             NVARCHAR(200)    NOT NULL,
    dataset_size_bytes      BIGINT           NOT NULL,
    dataset_item_count      INT              NOT NULL,
    dataset_seed             INT              NOT NULL,
    warmup_iterations       INT              NOT NULL,
    iterations               INT              NOT NULL,
    schema_version           INT              NOT NULL,
    recorded_at_utc         DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_performance_benchmark_runs PRIMARY KEY (run_id),
    -- Alvo da FK composta de performance_benchmark_measurements (defesa em profundidade — mesmo padrão de
    -- UQ_jobs_identity em 0001): uma medição só pode referenciar uma execução do MESMO tenant/projeto.
    CONSTRAINT UQ_performance_benchmark_runs_identity UNIQUE (run_id, tenant_id, project_id),
    CONSTRAINT CK_pbr_dataset_size CHECK (dataset_size_bytes >= 0),
    CONSTRAINT CK_pbr_dataset_item_count CHECK (dataset_item_count >= 0),
    CONSTRAINT CK_pbr_warmup_iterations CHECK (warmup_iterations >= 0),
    CONSTRAINT CK_pbr_iterations CHECK (iterations >= 1),
    CONSTRAINT CK_pbr_schema_version CHECK (schema_version >= 1)
);
GO

-- Resolução de FindRecentAsync (execuções mais recentes de um cenário, no escopo autorizado).
CREATE INDEX IX_pbr_scenario ON dbo.performance_benchmark_runs
    (tenant_id, project_id, scenario_name, recorded_at_utc DESC);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma execução de benchmark é jamais atualizada ou apagada.
GRANT SELECT, INSERT ON dbo.performance_benchmark_runs TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.performance_benchmark_runs,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.performance_benchmark_runs AFTER INSERT;
GO

-- Uma linha por ITERAÇÃO medida (nunca inclui as iterações de aquecimento, descartadas antes de chegar à
-- Application). Apenas campos numéricos/enum — nenhuma mensagem de exceção, caminho ou texto livre que
-- pudesse carregar PII/segredo (mesmo invariante já reforçado em Domain.Performance.BenchmarkMeasurement).
CREATE TABLE dbo.performance_benchmark_measurements
(
    run_id                   UNIQUEIDENTIFIER NOT NULL,
    iteration_index          INT              NOT NULL,
    tenant_id                UNIQUEIDENTIFIER NOT NULL,
    project_id               UNIQUEIDENTIFIER NOT NULL,
    wall_clock_ms            FLOAT            NOT NULL,
    cpu_time_ms              FLOAT            NULL,
    peak_working_set_bytes   BIGINT           NULL,
    bytes_processed          BIGINT           NULL,
    items_processed          BIGINT           NULL,
    outcome                  TINYINT          NOT NULL,
    CONSTRAINT PK_performance_benchmark_measurements PRIMARY KEY (run_id, iteration_index),
    CONSTRAINT FK_pbm_run FOREIGN KEY (run_id, tenant_id, project_id)
        REFERENCES dbo.performance_benchmark_runs (run_id, tenant_id, project_id),
    CONSTRAINT CK_pbm_iteration_index CHECK (iteration_index >= 0),
    CONSTRAINT CK_pbm_wall_clock CHECK (wall_clock_ms >= 0),
    CONSTRAINT CK_pbm_cpu_time CHECK (cpu_time_ms IS NULL OR cpu_time_ms >= 0),
    CONSTRAINT CK_pbm_peak_ws CHECK (peak_working_set_bytes IS NULL OR peak_working_set_bytes >= 0),
    CONSTRAINT CK_pbm_bytes CHECK (bytes_processed IS NULL OR bytes_processed >= 0),
    CONSTRAINT CK_pbm_items CHECK (items_processed IS NULL OR items_processed >= 0),
    CONSTRAINT CK_pbm_outcome CHECK (outcome BETWEEN 0 AND 3)
);
GO

-- Resolução das medições de UMA execução (join simples por run_id, já escopado por tenant/projeto).
CREATE INDEX IX_pbm_run ON dbo.performance_benchmark_measurements (tenant_id, project_id, run_id);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma medição é jamais atualizada ou apagada.
GRANT SELECT, INSERT ON dbo.performance_benchmark_measurements TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.performance_benchmark_measurements,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.performance_benchmark_measurements AFTER INSERT;
GO
