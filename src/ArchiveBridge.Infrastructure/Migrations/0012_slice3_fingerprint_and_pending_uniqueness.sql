-- Slice 3 (1ª revisão técnica do PR #23) — fingerprint completo + unicidade de reserva pendente. Migração
-- ADITIVA e protegida por hash: só adiciona colunas/índice, sem alterar a migration 0011 já publicada e
-- sem operação destrutiva. Continua persistindo APENAS metadados/hashes.

-- content_sha256: SHA-256 do CONTEÚDO do evidence.json (distinto do hash semântico da evidência). A
-- reserva/finalização passam a validar os TRÊS hashes: configuration_hash (config completa),
-- evidence_hash (evidência semântica completa) e content_sha256 (bytes do artefato).
ALTER TABLE dbo.ev_discovery_runs ADD
    content_sha256 CHAR(64) NOT NULL
        CONSTRAINT DF_evd_content DEFAULT '0000000000000000000000000000000000000000000000000000000000000000';
GO

-- Unicidade da reserva PENDENTE por impressão digital completa: no máximo UMA versão Pending por
-- (tenant, projeto, ambiente, configuração, evidência semântica, conteúdo). Backstop SQL contra duas
-- reservas concorrentes da MESMA evidência (além do lock na transação de reserva).
CREATE UNIQUE INDEX UX_evd_pending_fingerprint
    ON dbo.ev_discovery_runs (tenant_id, project_id, environment_id, configuration_hash, evidence_hash, content_sha256)
    WHERE status = 0;
GO

-- Perfil e maturidade do adapter avaliado (registrados SEPARADAMENTE): runtime observado, documentação
-- oficial e homologação em laboratório. Nunca se grava laboratory_validated = 1 sem prova de laboratório.
ALTER TABLE dbo.ev_adapter_evaluations ADD
    profile_id             NVARCHAR(200) NULL,
    runtime_observed       BIT           NULL,
    official_documentation BIT           NULL,
    laboratory_validated   BIT           NULL;
GO

-- Categoria de erro estruturada por achado/capacidade (None=0, NotAvailable=1, PermissionDenied=2,
-- Timeout=3, OutputInvalid=4, ExecutionFailed=5).
ALTER TABLE dbo.ev_discovery_findings ADD
    error_category TINYINT NOT NULL CONSTRAINT DF_evfind_category DEFAULT 0;
GO
