-- I5/EPIC-06 Passo 4 — AB-I5-013 (decisão de engenharia sobre o bloqueio de AB-I5-012): correlaciona,
-- de forma IMUTÁVEL e append-only, cada dbo.wave_partition_output_bindings (o output físico canônico
-- verificado, 0028) com a exata dbo.wave_entries planejada (mailbox/PST) que ele serve. Sem esta coluna
-- não havia, em nenhuma camada do repositório, uma forma server-side de saber a qual mailbox de destino
-- um determinado PST fisicamente carregado pertence — o mapping CSV do Purview (Passo 4) não pode ser
-- gerado sem essa correlação (nunca infere por ordem, nome de arquivo, string de mailbox ou cronologia).
--
-- Decisão de engenharia (opção 2 do bloqueio AB-I5-012): estende o vínculo de EXECUÇÃO
-- (wave_partition_output_bindings), não dbo.wave_entries/dbo.wave_versions — WaveSelection continua sendo
-- PLANEJAMENTO; o vínculo é a fonte AUTORITATIVA de custódia física × destino. A identidade da entrada
-- (entry_id) é OPACA e DETERMINÍSTICA (WaveEntryId.Derive, Domain — hash SHA-256 de campos imutáveis da
-- WaveEntry + o wave_id), nunca um ID armazenado em dbo.wave_entries nem um índice/ordinal: por isso esta
-- migration NÃO altera dbo.wave_entries nem cria FK para lá — a Application recomputa e revalida o ID a
-- cada leitura a partir da seleção corrente (fail-closed se a entrada não for mais membro da onda).
--
-- Aditiva, append-only e não destrutiva: adiciona UMA coluna à tabela já existente. Como a tabela nunca
-- foi ligada a nenhuma composition root de produção (Passo 3 permanece não-conectado a nenhum host/worker
-- real neste repositório), está garantidamente vazia em qualquer ambiente real — por isso a coluna pode
-- ser NOT NULL sem DEFAULT (nenhuma linha preexistente a retrocompatibilizar). Nenhum DROP, nenhum UPDATE
-- de dados, nenhuma redefinição de 0001-0029 — os arquivos das migrations anteriores permanecem
-- byte-for-byte intactos.

ALTER TABLE dbo.wave_partition_output_bindings
    ADD entry_id CHAR(64) NOT NULL;
GO
