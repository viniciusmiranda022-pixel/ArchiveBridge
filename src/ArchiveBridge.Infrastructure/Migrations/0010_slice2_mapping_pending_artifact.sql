-- Slice 2 (4ª revisão técnica) — protocolo de publicação de artefato SEM I/O de filesystem sob a
-- transação SQL. Migration ADITIVA: apenas relaxa um CHECK (sem alterar migrations já aplicadas nem
-- fazer operação destrutiva). Persiste APENAS metadados de planejamento — nenhum conteúdo/segredo.

-- R5-B2 — Estado RESERVADO (PendingArtifact = 2) para mapping_csv_versions. O protocolo recuperável
-- passa a ser: (1) StageAsync fora da transação; (2) transação curta que valida o cercamento, reserva
-- o número de versão sob lock e insere a versão como PendingArtifact (2) — SEM nenhum I/O de
-- filesystem sob o lock; (3) PublishAsync fora da transação SQL (escreve manifesto + rename atômico);
-- (4) segunda transação curta que revalida o cercamento, promove PendingArtifact→Usable (0) e só então
-- marca a anterior como Superseded (1). O índice único filtrado UX_mcv_single_usable (status = 0)
-- continua garantindo no máximo UMA versão utilizável; uma versão PendingArtifact não conta.
--
-- CK_mcv_status precisa aceitar o novo estado (0..2). Como não se altera a migration 0004, troca-se o
-- constraint por um novo (drop + add) — operação não destrutiva sobre os dados existentes.
ALTER TABLE dbo.mapping_csv_versions DROP CONSTRAINT CK_mcv_status;
GO

ALTER TABLE dbo.mapping_csv_versions WITH CHECK
    ADD CONSTRAINT CK_mcv_status CHECK (status BETWEEN 0 AND 2);
GO
