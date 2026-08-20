-- Slice 4B (Passo 2) — Partition Planning: planos de particionamento determinísticos, persistidos e
-- auditáveis. NENHUMA execução de particionamento existe nesta fatia: o plano é INTENÇÃO/EVIDÊNCIA e o PST
-- permanece byte-for-byte inalterado.
--
-- Aditiva, append-only e não destrutiva: cria duas tabelas novas e adiciona UMA constraint UNIQUE à
-- dbo.pst_inspections (necessária para o FK composto que amarra o plano à inspeção canônica DENTRO do mesmo
-- tenant/projeto/artefato). Nenhum DROP, nenhum UPDATE de dados, nenhuma redefinição de 0001-0020 — os
-- arquivos das migrations anteriores permanecem byte-for-byte intactos.
--
-- Persiste APENAS metadados estruturais/decisórios (hashes determinísticos, tamanho, limites de política,
-- desfecho/motivo sanitizados, nome/versão de engine e planner): NUNCA conteúdo de e-mail/PST, assunto,
-- corpo, destinatário, nome de anexo, caminho físico, segredo ou token. Enums são persistidos como TINYINT
-- com o MESMO valor numérico do domínio:
--   PartitionPlanOutcome: Planned=0, Unsupported=1, Blocked=2
--   PartitionPlanReason:  SinglePartWithinTarget=0, ItemInventoryUnavailable=1,
--                         CanonicalInspectionUnavailable=2, SourceHashDivergence=3,
--                         StructuralDiagnosticNotValid=4, ObservedSizeUnavailable=5

-- Chave necessária para o FK composto de dbo.pst_partition_plans reforçar, no BANCO, que a inspeção
-- referenciada pertence exatamente ao mesmo tenant/projeto/artefato do plano (nunca cruza escopo).
ALTER TABLE dbo.pst_inspections
    ADD CONSTRAINT UQ_pst_inspections_scope UNIQUE (inspection_id, tenant_id, project_id, artifact_id);
GO

-- Planos de particionamento (dbo.pst_partition_plans): append-only, uma linha por AVALIAÇÃO. Só um plano
-- concluído (outcome = 0) COM partes é canônico/reaproveitável — reforçado pela coluna is_canonical (escrita
-- pela aplicação com o mesmo valor que PartitionPlan.IsCanonical calcula no Domain), pelo CHECK que impede
-- gravar valor divergente da regra e pelo índice único filtrado sobre ela.
CREATE TABLE dbo.pst_partition_plans
(
    plan_id             UNIQUEIDENTIFIER NOT NULL,
    tenant_id           UNIQUEIDENTIFIER NOT NULL,
    project_id          UNIQUEIDENTIFIER NOT NULL,
    artifact_id         UNIQUEIDENTIFIER NOT NULL,
    -- Inspeção canônica que originou o plano; NULL apenas quando não existe nenhuma (outcome = 2).
    inspection_id       UNIQUEIDENTIFIER NULL,
    source_hash         CHAR(64)         NOT NULL,
    source_size_bytes   BIGINT           NULL,
    -- Identidade determinística das ENTRADAS (runbook §20.2) e fingerprint da política (§20.1).
    plan_hash           CHAR(64)         NOT NULL,
    policy_fingerprint  CHAR(64)         NOT NULL,
    target_part_bytes   BIGINT           NOT NULL,
    hard_part_bytes     BIGINT           NOT NULL,
    planner_name        NVARCHAR(100)    NOT NULL,
    planner_version     NVARCHAR(50)     NOT NULL,
    engine_name         NVARCHAR(100)    NULL,
    engine_version      NVARCHAR(50)     NULL,
    outcome             TINYINT          NOT NULL,
    reason              TINYINT          NOT NULL,
    -- part_count é gravado junto do plano (e não derivado por COUNT) porque um CHECK constraint não pode
    -- consultar outra tabela: é ele que permite ao BANCO reforçar "Unsupported/Blocked nunca tem partes" e
    -- a regra de canonicidade abaixo. Plano e partes são gravados na MESMA transação, então os dois nunca
    -- divergem (uma falha no meio do caminho desfaz ambos).
    part_count          INT              NOT NULL,
    is_canonical        BIT              NOT NULL,
    correlation_id      UNIQUEIDENTIFIER NOT NULL,
    planned_at_utc      DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_pst_partition_plans PRIMARY KEY (plan_id),
    -- Chave necessária para o FK composto do filho (partes) reforçar plan_id + tenant + projeto.
    CONSTRAINT UQ_pst_partition_plans_scope UNIQUE (plan_id, tenant_id, project_id),
    CONSTRAINT FK_pst_partition_plans_artifact FOREIGN KEY (artifact_id, tenant_id, project_id)
        REFERENCES dbo.pst_artifacts (artifact_id, tenant_id, project_id),
    -- A inspeção referenciada é obrigatoriamente do MESMO tenant/projeto/artefato (anti cross-scope).
    CONSTRAINT FK_pst_partition_plans_inspection FOREIGN KEY (inspection_id, tenant_id, project_id, artifact_id)
        REFERENCES dbo.pst_inspections (inspection_id, tenant_id, project_id, artifact_id),
    CONSTRAINT CK_pst_partition_plans_outcome CHECK (outcome BETWEEN 0 AND 2),
    CONSTRAINT CK_pst_partition_plans_reason CHECK (reason BETWEEN 0 AND 5),
    -- Desfecho é SEMPRE derivado do motivo (PartitionPlanReasons.OutcomeOf no Domain) — travado no banco
    -- para que motivo e desfecho jamais possam divergir por bug futuro da aplicação.
    CONSTRAINT CK_pst_partition_plans_outcome_reason CHECK (
        outcome = CASE reason WHEN 0 THEN 0 WHEN 1 THEN 1 ELSE 2 END),
    CONSTRAINT CK_pst_partition_plans_policy CHECK (
        target_part_bytes > 0 AND hard_part_bytes > 0 AND target_part_bytes <= hard_part_bytes),
    CONSTRAINT CK_pst_partition_plans_size CHECK (source_size_bytes IS NULL OR source_size_bytes >= 0),
    CONSTRAINT CK_pst_partition_plans_part_count CHECK (part_count >= 0),
    -- Coerência por desfecho: um plano concluído (0) exige inspeção canônica, tamanho observado, engine
    -- identificada e ao menos uma parte. Unsupported (1)/Blocked (2) NUNCA têm partes — nenhum boundary,
    -- contagem ou offset é inventado quando falta informação.
    CONSTRAINT CK_pst_partition_plans_outcome_fields CHECK (
        (outcome = 0 AND inspection_id IS NOT NULL AND source_size_bytes IS NOT NULL
            AND engine_name IS NOT NULL AND engine_version IS NOT NULL AND part_count >= 1)
        OR (outcome <> 0 AND part_count = 0))
);
GO

-- Canonicidade reforçada como COLUNA explícita gravada pela aplicação (mesmo desenho já validado na
-- migration 0020 para pst_inspections): o SQL Server proíbe coluna computada no predicado de índice
-- filtrado, então o CHECK abaixo trava que o valor gravado é sempre consistente com outcome/part_count.
ALTER TABLE dbo.pst_partition_plans ADD CONSTRAINT CK_pst_partition_plans_is_canonical CHECK (
    is_canonical = CASE WHEN outcome = 0 AND part_count >= 1 THEN CONVERT(BIT, 1) ELSE CONVERT(BIT, 0) END
);
GO

-- Idempotência: existe no máximo UM plano canônico por artefato + identidade determinística de entrada
-- (plan_hash já embute tenant/projeto/artefato/inspeção/hash de origem/policy fingerprint/planner). Mudar
-- hash de origem ou policy/config muda o plan_hash e portanto NUNCA reaproveita um plano obsoleto.
-- Unsupported/Blocked (is_canonical = 0) nunca ocupam nem disputam este índice.
CREATE UNIQUE INDEX UX_pst_partition_plans_canonical
    ON dbo.pst_partition_plans (tenant_id, project_id, artifact_id, plan_hash)
    WHERE is_canonical = 1;
GO

-- Índice de consulta do histórico de avaliações por artefato (evidência/auditoria), mais recente primeiro.
CREATE INDEX IX_pst_partition_plans_artifact
    ON dbo.pst_partition_plans (tenant_id, project_id, artifact_id, planned_at_utc DESC);
GO

-- Partes planejadas (dbo.pst_partition_plan_parts): INTENÇÃO, não execução. part_key é opaco (derivado do
-- plan_hash + sequência) — nunca UPN, caminho, nome de arquivo ou qualquer dado de mailbox.
CREATE TABLE dbo.pst_partition_plan_parts
(
    part_id              UNIQUEIDENTIFIER NOT NULL,
    plan_id              UNIQUEIDENTIFIER NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    part_sequence        INT              NOT NULL,
    part_key             CHAR(64)         NOT NULL,
    planned_size_bytes   BIGINT           NOT NULL,
    covers_entire_source BIT              NOT NULL,
    CONSTRAINT PK_pst_partition_plan_parts PRIMARY KEY (part_id),
    CONSTRAINT FK_pst_partition_plan_parts_plan FOREIGN KEY (plan_id, tenant_id, project_id)
        REFERENCES dbo.pst_partition_plans (plan_id, tenant_id, project_id),
    CONSTRAINT UQ_pst_partition_plan_parts_sequence UNIQUE (plan_id, part_sequence),
    CONSTRAINT UQ_pst_partition_plan_parts_key UNIQUE (plan_id, part_key),
    CONSTRAINT CK_pst_partition_plan_parts_sequence CHECK (part_sequence >= 1),
    CONSTRAINT CK_pst_partition_plan_parts_size CHECK (planned_size_bytes >= 0)
);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE. Manutenção NÃO recebe grant algum.
GRANT SELECT, INSERT ON dbo.pst_partition_plans TO ab_app_role;
GRANT SELECT, INSERT ON dbo.pst_partition_plan_parts TO ab_app_role;
GO

-- Isolamento por tenant (RLS): as duas tabelas participam integralmente da política existente.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_partition_plans,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_partition_plans AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_partition_plan_parts,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_partition_plan_parts AFTER INSERT;
GO
