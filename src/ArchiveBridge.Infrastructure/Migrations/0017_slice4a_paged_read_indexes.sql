-- Slice 4A — Passo 5: índices de suporte às consultas paginadas (keyset/seek) de evidências e auditoria.
--
-- Aditiva e NÃO destrutiva: apenas CREATE INDEX. Não altera 0001–0016, não faz DROP, não recria tabela e
-- não altera dados. Cada índice alinha exatamente a chave de ORDER BY/seek da respectiva consulta, evitando
-- OFFSET/varredura e mantendo o custo estável sob inserções concorrentes (tabelas append-only/históricas).

-- (A) HISTÓRICO DE EVIDÊNCIAS — seek por completed_at_utc DESC, environment_id DESC, discovery_version DESC
-- dentro do escopo (tenant_id, project_id). INCLUDE mínimo: apenas as duas colunas TINYINT usadas como
-- predicado residual (status NOT IN (0,1) e o filtro opcional de result_status), evitando lookup para
-- avaliá-las. A projeção restante resolve por lookup na PK (environment_id, discovery_version), limitada ao
-- tamanho de página (<= 100).
CREATE INDEX IX_evd_scope_completed
    ON dbo.ev_discovery_runs (tenant_id, project_id, completed_at_utc DESC, environment_id DESC, discovery_version DESC)
    INCLUDE (status, result_status);
GO

-- (B) AUTENTICAÇÃO — o índice existente IX_portal_sign_in_events_tenant_time cobre (tenant_id,
-- occurred_at_utc DESC) mas NÃO carrega o desempate por event_id exigido pelo cursor. Adiciona-se um índice
-- alinhado ao seek (tenant_id, occurred_at_utc DESC, event_id DESC). O índice antigo é preservado.
-- (A trilha OPERACIONAL já possui IX_portal_operational_audit_scope_time com tenant_id, project_id,
-- occurred_at_utc DESC, event_id DESC — nenhum índice novo é necessário para ela.)
CREATE INDEX IX_portal_sign_in_events_tenant_time_event
    ON dbo.portal_sign_in_events (tenant_id, occurred_at_utc DESC, event_id DESC);
GO
