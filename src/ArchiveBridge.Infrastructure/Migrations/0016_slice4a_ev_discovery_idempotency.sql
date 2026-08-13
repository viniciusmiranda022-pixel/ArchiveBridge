-- Slice 4A — idempotência do disparo de descoberta EV pelo Portal.
--
-- Aditiva e não destrutiva: adiciona uma chave de idempotência OPCIONAL a ev_discovery_commands e um
-- índice ÚNICO FILTRADO que serve de backstop SQL contra corrida (duas requisições concorrentes com a
-- mesma tenant/projeto/chave não podem criar dois comandos). Linhas históricas permanecem com a chave
-- NULL (o índice filtrado as ignora). NÃO altera 0011–0015 nem qualquer objeto existente.

ALTER TABLE dbo.ev_discovery_commands ADD idempotency_key UNIQUEIDENTIFIER NULL;
GO

CREATE UNIQUE INDEX UX_ev_discovery_commands_idempotency
    ON dbo.ev_discovery_commands (tenant_id, project_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;
GO
