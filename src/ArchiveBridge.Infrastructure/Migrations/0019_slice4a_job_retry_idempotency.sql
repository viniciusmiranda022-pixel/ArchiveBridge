-- Slice 4A Passo 7 — idempotência do retry autorizado solicitado pelo Portal.
--
-- Aditiva e não destrutiva, no MESMO padrão já aceito pela migration 0016 (idempotência do disparo de
-- descoberta EV): adiciona uma chave de idempotência OPCIONAL diretamente a dbo.jobs (o retry autorizado
-- age sobre um Job JÁ EXISTENTE — não cria um novo comando/recurso, então não há necessidade arquitetural
-- de uma tabela paralela) e um índice ÚNICO FILTRADO que serve de backstop SQL contra reuso indevido da
-- MESMA chave para um Job DIFERENTE (conflito determinístico via violação de unicidade). A chave reflete
-- apenas a solicitação de retry MAIS RECENTE aceita para o Job — suficiente para o propósito de proteção
-- contra double-submit/replay da MESMA renderização de formulário (mesmo padrão de "uma chave por
-- renderização" já usado em EV Discovery e Mapping Upload); não é um livro-razão histórico de chaves.
-- Linhas existentes permanecem com a chave NULL (o índice filtrado as ignora). NÃO altera 0001–0018 nem
-- qualquer objeto existente.

ALTER TABLE dbo.jobs ADD retry_idempotency_key UNIQUEIDENTIFIER NULL;
GO

CREATE UNIQUE INDEX UX_jobs_retry_idempotency
    ON dbo.jobs (tenant_id, project_id, retry_idempotency_key)
    WHERE retry_idempotency_key IS NOT NULL;
GO
