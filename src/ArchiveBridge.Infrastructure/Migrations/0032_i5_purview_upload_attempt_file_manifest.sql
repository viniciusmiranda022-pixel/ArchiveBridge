-- I5/EPIC-06 Passo 4 — AB-I5-015: manifestação determinística por arquivo da evidência de upload Purview
-- (blocker de cadeia de custódia sobre AB-I5-012/013). A evidência agregada existente
-- (dbo.purview_upload_attempts.expected_file_count/expected_total_bytes) prova apenas CONTAGEM e SOMA de
-- bytes — dois conjuntos DIFERENTES de PSTs que coincidam em quantidade e soma podem satisfazer aquela
-- validação e produzir mapping para arquivos que não estão individualmente comprovados pelo upload
-- verificado. Esta migration adiciona a manifestação item-a-item (execução/binding, nome remoto, hash e
-- tamanho) exigida para a correspondência EXATA 1:1 que ResolvePurviewMappingEvidenceUseCase agora impõe.
--
-- Aditiva, append-only e não destrutiva: adiciona UMA coluna (manifest_hash) e UMA constraint UNIQUE à
-- tabela já existente dbo.purview_upload_attempts (necessária para o FK composto do novo child table,
-- mesmo padrão já usado em 0028 para dbo.pst_partition_executions) e cria UMA tabela nova
-- (dbo.purview_upload_attempt_manifest_items). Nenhum DROP, nenhum UPDATE de dados, nenhuma redefinição de
-- 0001-0031 — os arquivos das migrations anteriores permanecem byte-for-byte intactos. Como
-- dbo.purview_upload_attempts nunca foi ligada a nenhuma composition root de produção neste repositório
-- (mesmo estado de dbo.wave_partition_output_bindings antes da 0030), está garantidamente vazia em
-- qualquer ambiente real — por isso manifest_hash pode ser adicionada NOT NULL-quando-Uploaded sem
-- backfill.
--
-- Persiste APENAS identidades opacas (execution_id), o nome remoto EXATO já usado pelo AzCopy real e o
-- hash/tamanho canônicos já existentes na execução de origem — NUNCA caminho físico/absoluto, mailbox/UPN
-- ou segredo. manifest_hash detecta adulteração de qualquer item (inserido, removido, duplicado ou
-- alterado) no rehydrate (mesmo princípio de binding_hash/handle_hash).

-- Chave necessária para o FK composto do novo child table reforçar, no BANCO, que a tentativa referenciada
-- pertence exatamente ao mesmo tenant/projeto do item de manifestação (nunca cruza escopo).
ALTER TABLE dbo.purview_upload_attempts
    ADD CONSTRAINT UQ_purview_upload_attempts_scope UNIQUE (attempt_id, tenant_id, project_id);
GO

ALTER TABLE dbo.purview_upload_attempts
    ADD manifest_hash CHAR(64) NULL;
GO

-- Mesmo princípio de CK_purview_upload_attempts_evidence_only_when_uploaded (0029): a manifestação só
-- existe quando o desfecho é Uploaded (UploadVerified) — nenhum outro desfecho pode carregar um hash que
-- sugira evidência de sucesso parcial.
ALTER TABLE dbo.purview_upload_attempts
    ADD CONSTRAINT CK_purview_upload_attempts_manifest_only_when_uploaded CHECK (
        (outcome = 0 AND manifest_hash IS NOT NULL) OR (outcome <> 0 AND manifest_hash IS NULL)
    );
GO

-- Manifestação item-a-item (AB-I5-015 item 1): UMA linha por PST efetivamente coberto pelo transporte
-- comprovado desta tentativa. Append-only, mesma tentativa nunca reescrita.
CREATE TABLE dbo.purview_upload_attempt_manifest_items
(
    attempt_id           UNIQUEIDENTIFIER NOT NULL,
    tenant_id            UNIQUEIDENTIFIER NOT NULL,
    project_id           UNIQUEIDENTIFIER NOT NULL,
    -- Ordinal de ARMAZENAMENTO/leitura estável (ordem canônica por execution_id no momento da gravação) —
    -- NUNCA usado como identidade do item; a identidade é execution_id (UX_puami_execution abaixo).
    item_index           INT              NOT NULL,
    execution_id         UNIQUEIDENTIFIER NOT NULL,
    remote_pst_name      NVARCHAR(300)    NOT NULL,
    output_hash          CHAR(64)         NOT NULL,
    expected_size_bytes  BIGINT           NOT NULL,
    CONSTRAINT PK_purview_upload_attempt_manifest_items PRIMARY KEY (attempt_id, item_index),
    CONSTRAINT FK_puami_attempt FOREIGN KEY (attempt_id, tenant_id, project_id)
        REFERENCES dbo.purview_upload_attempts (attempt_id, tenant_id, project_id),
    CONSTRAINT CK_puami_size CHECK (expected_size_bytes >= 0),
    -- No máximo UM item por execução dentro da MESMA tentativa (backstop de duplicidade — reforça, no
    -- banco, o mesmo invariante já validado no Domain por PurviewUploadEvidence).
    CONSTRAINT UX_puami_execution UNIQUE (attempt_id, execution_id)
);
GO

-- Índice de leitura ordenada (item_index) da manifestação de uma tentativa.
CREATE INDEX IX_puami_attempt ON dbo.purview_upload_attempt_manifest_items (tenant_id, project_id, attempt_id, item_index);
GO

-- Append-only: apenas SELECT/INSERT à aplicação; nenhum UPDATE/DELETE (mesmo padrão de
-- dbo.purview_upload_attempts — evidência estrutural, sem necessidade operacional de leitura pela
-- identidade de manutenção).
GRANT SELECT, INSERT ON dbo.purview_upload_attempt_manifest_items TO ab_app_role;
GO

-- Isolamento por tenant (RLS): a tabela nova participa integralmente da política existente. Isolamento POR
-- PROJETO é reforçado pelo filtro explícito por project_id em toda query (Contracts/Infrastructure) e pela
-- PK/UNIQUE composta.
ALTER SECURITY POLICY rls.tenant_isolation_policy
    ADD FILTER PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_upload_attempt_manifest_items,
    ADD BLOCK PREDICATE rls.fn_tenant_access(tenant_id) ON dbo.purview_upload_attempt_manifest_items AFTER INSERT;
GO
