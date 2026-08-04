-- Slice 2 (2ª revisão técnica) — endereça os blockers remanescentes SEM alterar migrations já
-- aplicadas (adiciona objetos novos; sem alteração destrutiva). Persiste APENAS metadados de
-- planejamento/governança: nenhum conteúdo de e-mail/PST, segredo, SAS ou token.
--   capacity_assessment_decisions.decision: Approved=0, Rejected=1

-- B1 — Contexto do comando VINCULADO À VERSÃO. O enfileiramento fixa a versão/estado que originou o
-- comando; o worker recusa (STALE_COMMAND_CONTEXT) se divergir na execução. Campos opcionais (nem
-- todo comando fixa todos): projeto (config), onda (seleção/destino), mapping (esquema/gerador/política).
ALTER TABLE dbo.planning_commands ADD
    command_schema_version                 INT           NOT NULL CONSTRAINT DF_pc_schema DEFAULT 1,
    expected_project_configuration_version INT           NULL,
    expected_configuration_hash            CHAR(64)      NULL,
    expected_wave_version                  INT           NULL,
    expected_selection_hash                CHAR(64)      NULL,
    expected_target_root_folder            NVARCHAR(400) NULL,
    mapping_schema_version                 INT           NULL,
    mapping_generator_version              INT           NULL,
    mapping_policy_version                 INT           NULL,
    CONSTRAINT CK_pc_schema_version CHECK (command_schema_version >= 1);
GO

-- B4/B5 — Impressão digital COMPLETA de geração (idempotência real) + referência ao artefato IMUTÁVEL
-- (mapping.csv/mapping.sha256/manifesto persistidos fora do SQL; aqui só o caminho lógico + tamanho).
ALTER TABLE dbo.mapping_csv_versions ADD
    generation_fingerprint CHAR(64)      NOT NULL CONSTRAINT DF_mcv_fingerprint DEFAULT '0000000000000000000000000000000000000000000000000000000000000000',
    artifact_path          NVARCHAR(400) NOT NULL CONSTRAINT DF_mcv_artifact_path DEFAULT N'',
    artifact_size_bytes    BIGINT        NOT NULL CONSTRAINT DF_mcv_artifact_size DEFAULT 0,
    CONSTRAINT CK_mcv_artifact_size CHECK (artifact_size_bytes >= 0);
GO

-- A aplicação também pode gravar o caminho/tamanho do artefato ao inserir a versão (já tem INSERT);
-- estes campos são imutáveis após a inserção (sem UPDATE além de 'status').

-- B8 — Resolução da identidade do archive: TRUE quando veio de manifesto autorizado (aliases
-- unificados); FALSE quando derivada só da mailbox. Uma entrada não resolvida bloqueia a validação.
ALTER TABLE dbo.wave_entries ADD
    is_archive_resolved BIT NOT NULL CONSTRAINT DF_we_resolved DEFAULT 0;
GO

-- B6 — Decisões de avaliação Microsoft (append-only). Somente uma decisão APROVADA, da versão
-- corrente e do archive avaliado, libera a onda (Blocked → Validating). Nunca altera/apaga avaliações.
CREATE TABLE dbo.capacity_assessment_decisions
(
    decision_id        BIGINT IDENTITY (1, 1) NOT NULL,
    tenant_id          UNIQUEIDENTIFIER       NOT NULL,
    project_id         UNIQUEIDENTIFIER       NOT NULL,
    wave_id            UNIQUEIDENTIFIER       NOT NULL,
    wave_version       INT                    NOT NULL,
    target_archive_id  NVARCHAR(320)          NOT NULL,
    decision           TINYINT                NOT NULL,
    evidence_reference NVARCHAR(200)          NOT NULL,
    decided_by         NVARCHAR(200)          NOT NULL,
    decided_at_utc     DATETIME2(3)           NOT NULL CONSTRAINT DF_cad_decided DEFAULT SYSUTCDATETIME(),
    correlation_id     UNIQUEIDENTIFIER       NOT NULL,
    notes              NVARCHAR(400)          NULL,
    CONSTRAINT PK_capacity_assessment_decisions PRIMARY KEY (decision_id),
    CONSTRAINT FK_cad_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    CONSTRAINT CK_cad_decision CHECK (decision BETWEEN 0 AND 1),
    CONSTRAINT CK_cad_version CHECK (wave_version >= 1)
);

CREATE INDEX IX_cad_scope
    ON dbo.capacity_assessment_decisions (tenant_id, project_id, wave_id, wave_version, target_archive_id);
GO

-- Append-only: a aplicação insere/le decisões; a manutenção apenas lê (auditoria).
GRANT SELECT, INSERT ON dbo.capacity_assessment_decisions TO ab_app_role;
GRANT SELECT ON dbo.capacity_assessment_decisions TO ab_maintenance_role;
GO

-- Isolamento por tenant (defesa em profundidade) na nova tabela.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.capacity_assessment_decisions,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.capacity_assessment_decisions AFTER INSERT;
GO
