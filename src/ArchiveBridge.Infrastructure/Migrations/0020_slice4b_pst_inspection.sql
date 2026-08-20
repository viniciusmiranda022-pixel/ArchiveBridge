-- Slice 4B (Passo 1) — PST Inspection & Inventory: custódia e checkpoints de inspeção read-only.
--
-- Aditiva, append-only e não destrutiva: cria duas tabelas novas (custódia + tentativas de inspeção), sem
-- DROP, sem alterar dados existentes e sem redefinir nenhuma tabela de 0001-0019. Persiste APENAS
-- metadados estruturais (hash, tamanho, diagnóstico sanitizado, variante de formato): NUNCA conteúdo de
-- e-mail/PST, assunto, corpo, anexo, segredo, SAS ou token. Enums são persistidos como TINYINT com o
-- MESMO valor numérico do domínio:
--   PstInspectionOutcome:      Completed=0, Stale=1, LimitExceeded=2
--   PstStructuralDiagnostic:   Valid=0, TooSmall=1, InvalidSignature=2, InvalidClientSignature=3,
--                              UnsupportedVersion=4, ReadError=5
--   PstFormatVariant:          Unknown=0, AnsiLegacy=1, Unicode2003To2010=2, Unicode2013Plus=3

-- Registro de custódia (dbo.pst_artifacts): a ligação SERVER-SIDE entre identidade opaca, escopo e
-- caminho relativo à raiz configurada. Imutável — nunca UPDATE. O hash/tamanho aqui são a baseline contra
-- a qual toda inspeção verifica staleness.
CREATE TABLE dbo.pst_artifacts
(
    artifact_id            UNIQUEIDENTIFIER NOT NULL,
    tenant_id              UNIQUEIDENTIFIER NOT NULL,
    project_id             UNIQUEIDENTIFIER NOT NULL,
    relative_path          NVARCHAR(400)    NOT NULL,
    registered_hash        CHAR(64)         NOT NULL,
    registered_size_bytes  BIGINT           NOT NULL,
    registered_at_utc      DATETIME2(3)     NOT NULL CONSTRAINT DF_pst_artifacts_registered DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_pst_artifacts PRIMARY KEY (artifact_id),
    -- Chave necessária para o FK composto do filho (pst_inspections) reforçar artifact_id + tenant + projeto.
    CONSTRAINT UQ_pst_artifacts_scope UNIQUE (artifact_id, tenant_id, project_id),
    -- O mesmo caminho relativo não pode ser registrado duas vezes no mesmo tenant/projeto (custódia única).
    CONSTRAINT UQ_pst_artifacts_path UNIQUE (tenant_id, project_id, relative_path),
    CONSTRAINT FK_pst_artifacts_project FOREIGN KEY (tenant_id, project_id)
        REFERENCES dbo.projects (tenant_id, project_id),
    CONSTRAINT CK_pst_artifacts_size CHECK (registered_size_bytes >= 0)
);
GO

-- Checkpoints de inspeção (dbo.pst_inspections): append-only, uma linha por TENTATIVA. Só uma tentativa
-- Completed cujo hash observado bate com expected_hash pode ser canônica — reforçado pelo índice único
-- filtrado abaixo (backstop de corrida além da revalidação em Application).
CREATE TABLE dbo.pst_inspections
(
    inspection_id       UNIQUEIDENTIFIER NOT NULL,
    tenant_id           UNIQUEIDENTIFIER NOT NULL,
    project_id          UNIQUEIDENTIFIER NOT NULL,
    artifact_id         UNIQUEIDENTIFIER NOT NULL,
    expected_hash       CHAR(64)         NOT NULL,
    observed_hash       CHAR(64)         NULL,
    observed_size_bytes BIGINT           NULL,
    outcome             TINYINT          NOT NULL,
    diagnostic          TINYINT          NULL,
    format_variant      TINYINT          NULL,
    engine_name         NVARCHAR(100)    NOT NULL,
    engine_version      NVARCHAR(50)     NOT NULL,
    correlation_id      UNIQUEIDENTIFIER NOT NULL,
    started_at_utc      DATETIME2(3)     NOT NULL,
    completed_at_utc    DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_pst_inspections PRIMARY KEY (inspection_id),
    -- FK composta ao artefato reforçando artifact_id + tenant + projeto (nunca cruza escopo).
    CONSTRAINT FK_pst_inspections_artifact FOREIGN KEY (artifact_id, tenant_id, project_id)
        REFERENCES dbo.pst_artifacts (artifact_id, tenant_id, project_id),
    CONSTRAINT CK_pst_inspections_outcome CHECK (outcome BETWEEN 0 AND 2),
    CONSTRAINT CK_pst_inspections_diagnostic CHECK (diagnostic IS NULL OR diagnostic BETWEEN 0 AND 5),
    CONSTRAINT CK_pst_inspections_format CHECK (format_variant IS NULL OR format_variant BETWEEN 0 AND 3),
    CONSTRAINT CK_pst_inspections_size CHECK (observed_size_bytes IS NULL OR observed_size_bytes >= 0),
    CONSTRAINT CK_pst_inspections_timestamps CHECK (completed_at_utc >= started_at_utc),
    -- Coerência por desfecho: Completed (0) sempre tem diagnostic/format_variant; hash/tamanho só faltam
    -- quando diagnostic = 5 (ReadError — leitura não concluída, nenhum hash confiável). Stale (1) sempre
    -- tem hash/tamanho observados (é POR ISSO que diverge) mas nunca diagnostic/format_variant.
    -- LimitExceeded (2) nunca tem hash observado (leitura não concluída) nem diagnostic/format_variant.
    CONSTRAINT CK_pst_inspections_outcome_fields CHECK (
        (outcome = 0 AND diagnostic IS NOT NULL AND format_variant IS NOT NULL AND (
            (diagnostic = 5 AND observed_hash IS NULL AND observed_size_bytes IS NULL)
            OR (diagnostic <> 5 AND observed_hash IS NOT NULL AND observed_size_bytes IS NOT NULL)))
        OR (outcome = 1 AND observed_hash IS NOT NULL AND observed_size_bytes IS NOT NULL
            AND diagnostic IS NULL AND format_variant IS NULL)
        OR (outcome = 2 AND observed_hash IS NULL AND observed_size_bytes IS NULL
            AND diagnostic IS NULL AND format_variant IS NULL))
);
GO

-- Idempotência: só existe UMA tentativa canônica (Completed, hash observado == esperado) por artefato.
CREATE UNIQUE INDEX UX_pst_inspections_canonical
    ON dbo.pst_inspections (tenant_id, project_id, artifact_id, expected_hash)
    WHERE outcome = 0;
GO

-- Índice de consulta do histórico de tentativas por artefato (evidência/auditoria), mais recente primeiro.
CREATE INDEX IX_pst_inspections_artifact
    ON dbo.pst_inspections (tenant_id, project_id, artifact_id, started_at_utc DESC);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE. Manutenção NÃO recebe grant algum.
GRANT SELECT, INSERT ON dbo.pst_artifacts TO ab_app_role;
GRANT SELECT, INSERT ON dbo.pst_inspections TO ab_app_role;
GO

-- Isolamento por tenant (RLS): as duas tabelas participam integralmente da política existente.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_artifacts,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_artifacts AFTER INSERT,
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_inspections,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.pst_inspections AFTER INSERT;
GO
