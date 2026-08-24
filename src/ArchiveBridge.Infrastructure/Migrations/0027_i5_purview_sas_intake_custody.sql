-- I5/EPIC-06 Passo 2 — Secure SAS Intake & Custody (AB-I5-004). Migração ADITIVA e protegida por hash: só
-- cria objetos novos, sem alterar migrations já aplicadas e sem operação destrutiva. Duas tabelas:
--   dbo.purview_sas_upload_handles  — METADADO opaco de custódia (estado, fingerprint não reversível,
--     referência opaca ao secret store, metadados NÃO secretos de host/container/expiry, linkage de
--     auditoria). NUNCA contém o SAS em texto claro nem ciphertext.
--   dbo.purview_sas_secret_material — o material protegido (DPAPI) do secret store. Diferente das demais
--     tabelas deste release, NÃO é append-only: permite DELETE explícito para a destruição local do item
--     12 do work order. Mesmo protegido (DPAPI), nenhum GRANT de leitura é concedido à identidade de
--     MANUTENÇÃO — apenas a aplicação, sob a identidade dedicada do workload, pode ler/gravar/apagar.
--   state (SasHandleState): Stored=0, Available=1, Consumed=2, Expired=3, Destroyed=4

-- Metadado de custódia, versionado por (tenant, projeto, wave) via generation — a linha é MUTADA nas
-- transições de ciclo de vida (mesmo padrão de concorrência otimista de dbo.migration_waves, row_version).
CREATE TABLE dbo.purview_sas_upload_handles
(
    handle_id                UNIQUEIDENTIFIER NOT NULL,
    tenant_id                UNIQUEIDENTIFIER NOT NULL,
    project_id                UNIQUEIDENTIFIER NOT NULL,
    wave_id                    UNIQUEIDENTIFIER NOT NULL,
    generation                 INT              NOT NULL,
    state                      TINYINT          NOT NULL,
    fingerprint                 CHAR(64)        NOT NULL,
    secret_store_reference       NVARCHAR(200)  NOT NULL,
    authorized_host               NVARCHAR(300) NOT NULL,
    authorized_container           NVARCHAR(100) NOT NULL,
    key_version                     INT          NULL,
    expires_at_utc                   DATETIME2(3) NOT NULL,
    stored_at_utc                     DATETIME2(3) NOT NULL,
    available_at_utc                   DATETIME2(3) NULL,
    consumed_at_utc                     DATETIME2(3) NULL,
    expired_at_utc                       DATETIME2(3) NULL,
    destroyed_at_utc                      DATETIME2(3) NULL,
    correlation_id                    UNIQUEIDENTIFIER NOT NULL,
    recorded_at_utc                     DATETIME2(3) NOT NULL,
    handle_hash                          CHAR(64)     NOT NULL,
    row_version                          ROWVERSION,
    CONSTRAINT PK_purview_sas_upload_handles PRIMARY KEY (handle_id),
    CONSTRAINT UQ_psuh_scope_generation UNIQUE (tenant_id, project_id, wave_id, generation),
    CONSTRAINT CK_psuh_state CHECK (state BETWEEN 0 AND 4),
    CONSTRAINT CK_psuh_generation CHECK (generation >= 1)
);

-- Backstop de canonicidade (item 16): no máximo UM handle "vivo" (Stored/Available/Consumed) por wave.
-- Expired/Destroyed (estados terminais) nunca disputam este índice — um novo intake sempre encontra a
-- geração anterior já fora dele antes de o candidato ser inserido (SqlPurviewSasUploadHandleStore marca o
-- anterior Destroyed na MESMA transação do insert da nova geração).
CREATE UNIQUE INDEX UX_psuh_canonical_live
    ON dbo.purview_sas_upload_handles (tenant_id, project_id, wave_id)
    WHERE state IN (0, 1, 2);

CREATE INDEX IX_psuh_latest ON dbo.purview_sas_upload_handles (tenant_id, project_id, wave_id, generation DESC);
GO

-- Material protegido (DPAPI) — referenciado apenas por secret_store_reference (opaco), nunca por FK
-- declarada (o handle e o material são gravados/lidos por operações INDEPENDENTES do secret store).
-- protection_scope documenta o DataProtectionScope usado (CurrentUser=0 — identidade dedicada do
-- workload, ADR-0008; LocalMachine=1 reservado, não usado pela baseline deste Passo).
CREATE TABLE dbo.purview_sas_secret_material
(
    reference_id      UNIQUEIDENTIFIER NOT NULL,
    tenant_id          UNIQUEIDENTIFIER NOT NULL,
    project_id          UNIQUEIDENTIFIER NOT NULL,
    protected_bytes       VARBINARY(4000) NOT NULL,
    entropy_bytes           VARBINARY(64) NOT NULL,
    protection_scope         TINYINT      NOT NULL,
    created_at_utc             DATETIME2(3) NOT NULL,
    CONSTRAINT PK_purview_sas_secret_material PRIMARY KEY (reference_id),
    CONSTRAINT CK_pssm_protection_scope CHECK (protection_scope IN (0, 1))
);
GO

-- Privilégios. purview_sas_upload_handles: a aplicação grava (append de nova geração) E atualiza (transição
-- de ciclo de vida na MESMA linha) — nunca DELETE. purview_sas_secret_material: a aplicação grava e APAGA
-- (destruição local explícita, item 12) — nunca UPDATE (o material de uma referência nunca é substituído
-- in-place; uma troca sempre cria uma referência nova). A identidade de MANUTENÇÃO não recebe NENHUM
-- privilégio em purview_sas_secret_material — mesmo protegido por DPAPI, o material fica fora do alcance
-- de leitura de qualquer identidade além da aplicação sob a identidade dedicada do workload.
GRANT SELECT, INSERT, UPDATE ON dbo.purview_sas_upload_handles TO ab_app_role;
GRANT SELECT ON dbo.purview_sas_upload_handles TO ab_maintenance_role;
GRANT SELECT, INSERT, DELETE ON dbo.purview_sas_secret_material TO ab_app_role;
GO

-- Isolamento por tenant (defesa em profundidade) nas duas tabelas novas. O isolamento POR PROJETO é
-- reforçado pelo filtro explícito por project_id em toda query (Contracts/Infrastructure) e pelas UNIQUE
-- compostas.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_sas_upload_handles,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_sas_upload_handles AFTER INSERT,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_sas_upload_handles AFTER UPDATE,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_sas_secret_material,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_sas_secret_material AFTER INSERT;
GO
