-- I6/EPIC-07 Passo 1 — AB-I6-001: fundação de evidência do import job do Purview (runbook §25.9/§26.1).
-- Introduz o plano determinístico do nome planejado do job (item 4) e as observações do operador sobre o
-- progresso do job real no portal (item 5) — nunca automatiza criação/validação/início do job (STOP-THE-LINE).
--
-- Aditiva, append-only e não destrutiva: cria TRÊS tabelas novas. Nenhum DROP, nenhum UPDATE de dados,
-- nenhuma redefinição de 0001-0032 — os arquivos das migrations anteriores permanecem byte-for-byte intactos.
--
-- dbo.purview_import_job_plans: UMA linha por tentativa de planejamento (wave_id, attempt_sequence),
-- nunca reescrita. dbo.purview_import_job_provider_bindings: no máximo UMA linha por plano, criada na
-- PRIMEIRA observação que traz um provider_operation_id — o índice único (tenant_id, project_id,
-- provider_operation_id) impede, no BANCO, que o MESMO provider_operation_id seja reivindicado por dois
-- planos diferentes do mesmo escopo (AB-I6-001 item 5, defesa em profundidade além da checagem da
-- Application). dbo.purview_import_job_observations: append-only, uma linha por observação transcrita.

CREATE TABLE dbo.purview_import_job_plans
(
    wave_id              UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence     INT              NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    planned_job_name     VARCHAR(100)     NOT NULL,
    evidence_fingerprint CHAR(64)         NOT NULL,
    created_by           NVARCHAR(200)    NOT NULL,
    created_at_utc       DATETIME2(3)     NOT NULL,
    plan_hash            CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_import_job_plans PRIMARY KEY (wave_id, attempt_sequence),
    CONSTRAINT UQ_pijp_scope UNIQUE (wave_id, attempt_sequence, tenant_id, project_id),
    CONSTRAINT UQ_pijp_planned_job_name UNIQUE (planned_job_name),
    CONSTRAINT FK_pijp_wave FOREIGN KEY (wave_id, tenant_id, project_id)
        REFERENCES dbo.migration_waves (wave_id, tenant_id, project_id),
    CONSTRAINT CK_pijp_attempt_sequence CHECK (attempt_sequence >= 1)
);
GO

CREATE INDEX IX_pijp_scope ON dbo.purview_import_job_plans (tenant_id, project_id, wave_id, attempt_sequence);
GO

-- Append-only: apenas SELECT/INSERT — o plano é imutável desde a criação (nenhum UPDATE/DELETE).
GRANT SELECT, INSERT ON dbo.purview_import_job_plans TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_import_job_plans,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_import_job_plans AFTER INSERT;
GO

-- No máximo UMA linha por plano: a identidade do provider fica "amarrada" ao plano na PRIMEIRA
-- observação e nunca muda depois (reassociação = nova linha de observação com provider_operation_id
-- divergente é recusada pela Application ANTES de tentar inserir aqui).
CREATE TABLE dbo.purview_import_job_provider_bindings
(
    wave_id               UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence      INT              NOT NULL,
    tenant_id             UNIQUEIDENTIFIER NOT NULL,
    project_id            UNIQUEIDENTIFIER NOT NULL,
    provider_operation_id NVARCHAR(300)    NOT NULL,
    bound_at_utc          DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_purview_import_job_provider_bindings PRIMARY KEY (wave_id, attempt_sequence),
    CONSTRAINT FK_pijpb_plan FOREIGN KEY (wave_id, attempt_sequence, tenant_id, project_id)
        REFERENCES dbo.purview_import_job_plans (wave_id, attempt_sequence, tenant_id, project_id),
    -- O CORAÇÃO do invariante fail-closed do item 5: o MESMO provider_operation_id nunca pode ser
    -- reivindicado por dois planos diferentes do mesmo tenant/projeto — o segundo INSERT perde a corrida
    -- no BANCO, não apenas na Application.
    CONSTRAINT UQ_pijpb_provider_operation_id UNIQUE (tenant_id, project_id, provider_operation_id)
);
GO

-- Append-only sob a ótica de conteúdo lógico: a aplicação só pode inserir a linha (primeira observação);
-- nenhum UPDATE/DELETE — reassociar exigiria apagar/alterar esta linha, o que a role nunca pode fazer.
GRANT SELECT, INSERT ON dbo.purview_import_job_provider_bindings TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_import_job_provider_bindings,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_import_job_provider_bindings AFTER INSERT;
GO

CREATE TABLE dbo.purview_import_job_observations
(
    -- Ordinal de ARMAZENAMENTO monotônico (nunca usado como identidade lógica — essa é observation_id):
    -- necessário para determinar "a observação mais recente" de forma determinística, já que
    -- DATETIME2(3) pode empatar entre linhas inseridas na mesma janela de milissegundo (mesmo problema já
    -- documentado para binding_hash/created_at_utc em WavePartitionOutputBinding).
    sequence_no            BIGINT           IDENTITY(1,1) NOT NULL,
    observation_id         UNIQUEIDENTIFIER NOT NULL,
    wave_id                UNIQUEIDENTIFIER NOT NULL,
    attempt_sequence       INT              NOT NULL,
    tenant_id              UNIQUEIDENTIFIER NOT NULL,
    project_id             UNIQUEIDENTIFIER NOT NULL,
    provider_operation_id  NVARCHAR(300)    NOT NULL,
    observed_status        TINYINT          NOT NULL, -- PurviewImportJobObservedStatus (0..5)
    observed_at_utc        DATETIME2(3)     NOT NULL,
    operator_label         NVARCHAR(200)    NOT NULL,
    recorded_at_utc        DATETIME2(3)     NOT NULL,
    observation_hash       CHAR(64)         NOT NULL,
    CONSTRAINT PK_purview_import_job_observations PRIMARY KEY (observation_id),
    CONSTRAINT FK_pijo_plan FOREIGN KEY (wave_id, attempt_sequence, tenant_id, project_id)
        REFERENCES dbo.purview_import_job_plans (wave_id, attempt_sequence, tenant_id, project_id),
    CONSTRAINT CK_pijo_status CHECK (observed_status BETWEEN 0 AND 5)
);
GO

CREATE INDEX IX_pijo_scope ON dbo.purview_import_job_observations (tenant_id, project_id, wave_id, attempt_sequence, sequence_no);
GO

-- Append-only: apenas SELECT/INSERT — nenhuma observação é jamais atualizada ou apagada.
GRANT SELECT, INSERT ON dbo.purview_import_job_observations TO ab_app_role;
GO

ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_import_job_observations,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_import_job_observations AFTER INSERT;
GO
