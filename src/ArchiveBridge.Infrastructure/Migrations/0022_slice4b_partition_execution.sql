-- Slice 4B (Passo 3) — Partition Execution Foundation: materializa e verifica, de forma imutável e
-- append-only, o ÚNICO caso já provado pelo planner (Planned / SinglePartWithinTarget). O manifesto durável
-- do "PartCreated" (runbook §20.4) só é gravado DEPOIS que o writer confirmou o output por
-- reabertura/reinspeção — nunca antes, nunca para uma tentativa fracassada/rejeitada.
--
-- Aditiva, append-only e não destrutiva: cria UMA tabela nova e adiciona UMA constraint UNIQUE à
-- dbo.pst_partition_plan_parts (necessária para o FK composto que amarra a execução à parte planejada
-- DENTRO do mesmo tenant/projeto/plano). Nenhum DROP, nenhum UPDATE de dados, nenhuma redefinição de
-- 0001-0021 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.
--
-- Persiste APENAS metadados estruturais/decisórios (hashes determinísticos, tamanhos, nome/versão de
-- executor): NUNCA caminho físico/absoluto, UPN/mailbox completo, assunto, corpo, nome de anexo, segredo ou
-- token, e NUNCA conteúdo de PST. O local físico do output é sempre derivado, em tempo de leitura, dos IDs
-- opacos já persistidos aqui (tenant/projeto/plano/part_key) — nada de caminho é gravado.

-- Chave necessária para o FK composto de dbo.pst_partition_executions reforçar, no BANCO, que a parte
-- referenciada pertence exatamente ao mesmo tenant/projeto/plano da execução (nunca cruza escopo). Mesmo
-- padrão já usado em 0021 para dbo.pst_inspections (UQ_pst_inspections_scope).
ALTER TABLE dbo.pst_partition_plan_parts
    ADD CONSTRAINT UQ_pst_partition_plan_parts_scope UNIQUE (part_id, plan_id, tenant_id, project_id);
GO

-- Execuções de partição (dbo.pst_partition_executions): append-only, mas — diferente de
-- pst_partition_plans/pst_inspections — TODA linha é canônica por construção: a Application só chama
-- SaveAsync depois que o writer materializou, reabriu/reinspecionou e confirmou o output (nenhuma linha de
-- tentativa fracassada é gravada). Por isso não há coluna is_canonical/outcome aqui: um índice único
-- (não filtrado) sobre (tenant, projeto, plano, parte) já é o backstop completo de idempotência/concorrência.
CREATE TABLE dbo.pst_partition_executions
(
    execution_id        UNIQUEIDENTIFIER NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    artifact_id          UNIQUEIDENTIFIER NOT NULL,
    plan_id              UNIQUEIDENTIFIER NOT NULL,
    part_id              UNIQUEIDENTIFIER NOT NULL,
    -- Identidade determinística do plano (runbook §20.2) e da parte (part_key opaco) — liga a execução
    -- exatamente ao plano/parte que a originou; nunca UPN, caminho ou nome de arquivo.
    plan_hash            CHAR(64)         NOT NULL,
    part_sequence        INT              NOT NULL,
    part_key             CHAR(64)         NOT NULL,
    source_hash          CHAR(64)         NOT NULL,
    source_size_bytes    BIGINT           NOT NULL,
    -- Hash/tamanho do output REALMENTE escrito, confirmados por reabertura/reinspeção antes deste INSERT.
    output_hash          CHAR(64)         NOT NULL,
    output_size_bytes    BIGINT           NOT NULL,
    executor_name        NVARCHAR(100)    NOT NULL,
    executor_version     NVARCHAR(50)     NOT NULL,
    correlation_id       UNIQUEIDENTIFIER NOT NULL,
    started_at_utc       DATETIME2(3)     NOT NULL,
    completed_at_utc     DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_pst_partition_executions PRIMARY KEY (execution_id),
    CONSTRAINT FK_pst_partition_executions_plan FOREIGN KEY (plan_id, tenant_id, project_id)
        REFERENCES dbo.pst_partition_plans (plan_id, tenant_id, project_id),
    -- A parte referenciada é obrigatoriamente do MESMO tenant/projeto/plano (anti cross-scope).
    CONSTRAINT FK_pst_partition_executions_part FOREIGN KEY (part_id, plan_id, tenant_id, project_id)
        REFERENCES dbo.pst_partition_plan_parts (part_id, plan_id, tenant_id, project_id),
    CONSTRAINT FK_pst_partition_executions_artifact FOREIGN KEY (artifact_id, tenant_id, project_id)
        REFERENCES dbo.pst_artifacts (artifact_id, tenant_id, project_id),
    CONSTRAINT CK_pst_partition_executions_sequence CHECK (part_sequence >= 1),
    CONSTRAINT CK_pst_partition_executions_source_size CHECK (source_size_bytes >= 0),
    CONSTRAINT CK_pst_partition_executions_output_size CHECK (output_size_bytes >= 0),
    -- Invariante do único caso autorizado neste Passo (SinglePartWithinTarget cobre a origem INTEIRA): a
    -- saída tem de ser cópia byte-for-byte exata da origem. Reforçado também no Domain (defesa em
    -- profundidade); nenhuma das duas camadas confia cegamente na outra.
    CONSTRAINT CK_pst_partition_executions_byte_identical CHECK (
        output_hash = source_hash AND output_size_bytes = source_size_bytes),
    CONSTRAINT CK_pst_partition_executions_timestamps CHECK (completed_at_utc >= started_at_utc),
    -- No máximo UMA execução canônica por (tenant, projeto, plano, parte) — o backstop de corrida quando
    -- duas execuções concorrentes do mesmo plano tentam persistir o resultado ao mesmo tempo.
    CONSTRAINT UX_pst_partition_executions_canonical UNIQUE (tenant_id, project_id, plan_id, part_id)
);
GO

-- Índice de consulta do histórico de execuções por artefato (evidência/auditoria), mais recente primeiro.
CREATE INDEX IX_pst_partition_executions_artifact
    ON dbo.pst_partition_executions (tenant_id, project_id, artifact_id, completed_at_utc DESC);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE. Manutenção NÃO recebe grant algum.
GRANT SELECT, INSERT ON dbo.pst_partition_executions TO ab_app_role;
GO

-- Isolamento por tenant (RLS): a tabela participa integralmente da política existente.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_partition_executions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_partition_executions AFTER INSERT;
GO
