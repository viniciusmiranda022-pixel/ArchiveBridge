-- I5/EPIC-06 Passo 3 — bridging AB-I5-010: vínculo IMUTÁVEL e append-only entre uma onda aprovada
-- (dbo.migration_waves) e um output canônico de particionamento verificado (dbo.pst_partition_executions).
-- Desbloqueia AB-I5-009 item 2 ("nenhum path, URL SAS, storage host, filename ou project/wave scope vindo
-- do caller pode ser tratado como autoridade"): sem este vínculo não havia, em nenhuma camada do
-- repositório, uma fonte de autoridade server-side ligando a seleção de onda (planejamento, nunca
-- revalidada fisicamente) aos PST parts fisicamente verificados/particionados que a pipeline de custódia
-- (Slice 4B) produz.
--
-- Aditiva, append-only e não destrutiva: cria UMA tabela nova e adiciona UMA constraint UNIQUE à
-- dbo.pst_partition_executions (necessária para o FK composto que amarra o vínculo à execução DENTRO do
-- mesmo tenant/projeto — mesmo padrão já usado em 0022 para dbo.pst_partition_plan_parts). Nenhum DROP,
-- nenhum UPDATE de dados, nenhuma redefinição de 0001-0027 — os arquivos das migrations anteriores
-- permanecem byte-for-byte intactos.
--
-- Persiste APENAS IDs opacos e evidência hash/tamanho já existente nas tabelas de origem — NUNCA caminho
-- físico/absoluto, UPN/mailbox, SAS ou qualquer segredo. O binding_hash detecta adulteração de qualquer
-- campo persistido (mesmo princípio de dbo.purview_sas_upload_handles/dbo.pst_partition_executions).

-- Chave necessária para o FK composto de dbo.wave_partition_output_bindings reforçar, no BANCO, que a
-- execução referenciada pertence exatamente ao mesmo tenant/projeto do vínculo (nunca cruza escopo). Mesmo
-- padrão já usado em 0022 para dbo.pst_partition_plan_parts (UQ_pst_partition_plan_parts_scope).
ALTER TABLE dbo.pst_partition_executions
    ADD CONSTRAINT UQ_pst_partition_executions_scope UNIQUE (execution_id, tenant_id, project_id);
GO

-- Vínculos wave -> output de particionamento (dbo.wave_partition_output_bindings): append-only, mas toda
-- linha é canônica por construção — a Application só chama SaveAsync depois de resolver a onda via
-- IWaveStore e a execução via IPartitionExecutionStore.FindCanonicalAsync (ambos os únicos stores
-- server-side autorizados). Por isso não há coluna de estado/outcome aqui: um índice único (não filtrado)
-- sobre (tenant, projeto, wave, plano, parte) já é o backstop completo de idempotência/concorrência.
CREATE TABLE dbo.wave_partition_output_bindings
(
    binding_id           UNIQUEIDENTIFIER NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    wave_id              UNIQUEIDENTIFIER NOT NULL,
    plan_id              UNIQUEIDENTIFIER NOT NULL,
    part_id              UNIQUEIDENTIFIER NOT NULL,
    execution_id         UNIQUEIDENTIFIER NOT NULL,
    artifact_id          UNIQUEIDENTIFIER NOT NULL,
    -- Reidratados da execução canônica no momento do vínculo (nunca informados pelo caller) — defesa em
    -- profundidade e material da identidade lógica do upload (AB-I5-009 item 14).
    part_key             CHAR(64)         NOT NULL,
    output_hash          CHAR(64)         NOT NULL,
    output_size_bytes    BIGINT           NOT NULL,
    correlation_id       UNIQUEIDENTIFIER NOT NULL,
    created_at_utc       DATETIME2(3)     NOT NULL,
    binding_hash          CHAR(64)        NOT NULL,
    CONSTRAINT PK_wave_partition_output_bindings PRIMARY KEY (binding_id),
    CONSTRAINT FK_wpob_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    -- A execução referenciada é obrigatoriamente do MESMO tenant/projeto (anti cross-scope).
    CONSTRAINT FK_wpob_execution FOREIGN KEY (execution_id, tenant_id, project_id)
        REFERENCES dbo.pst_partition_executions (execution_id, tenant_id, project_id),
    -- A parte planejada referenciada é obrigatoriamente do MESMO tenant/projeto/plano.
    CONSTRAINT FK_wpob_plan_part FOREIGN KEY (part_id, plan_id, tenant_id, project_id)
        REFERENCES dbo.pst_partition_plan_parts (part_id, plan_id, tenant_id, project_id),
    CONSTRAINT CK_wpob_output_size CHECK (output_size_bytes >= 0),
    -- No máximo UM vínculo canônico por (tenant, projeto, wave, plano, parte) — o backstop de corrida
    -- quando duas criações concorrentes do mesmo vínculo tentam persistir ao mesmo tempo (idempotência,
    -- AB-I5-010 item 4).
    CONSTRAINT UX_wpob_canonical UNIQUE (tenant_id, project_id, wave_id, plan_id, part_id)
);
GO

-- Índice de consulta: todos os vínculos de uma onda (a lista consumida pelo upload worker antes do
-- AzCopy), mais antigo primeiro (ordem estável de upload).
CREATE INDEX IX_wpob_wave ON dbo.wave_partition_output_bindings (tenant_id, project_id, wave_id, created_at_utc ASC);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE. Manutenção NÃO recebe grant algum
-- (mesmo padrão de dbo.pst_partition_executions — evidência estrutural, não segredo, mas sem necessidade
-- operacional de acesso pela identidade de manutenção).
GRANT SELECT, INSERT ON dbo.wave_partition_output_bindings TO ab_app_role;
GO

-- Isolamento por tenant (RLS): a tabela participa integralmente da política existente. Isolamento POR
-- PROJETO é reforçado pelo filtro explícito por project_id em toda query (Contracts/Infrastructure) e pela
-- UNIQUE composta.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.wave_partition_output_bindings,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.wave_partition_output_bindings AFTER INSERT;
GO
