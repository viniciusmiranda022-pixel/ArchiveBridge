-- Slice 3 (3ª revisão técnica do PR #23) — maturidade de validação por FIXTURES automatizados. Migração
-- ADITIVA e protegida por hash: só adiciona uma coluna, sem alterar as migrations 0011/0012 já publicadas e
-- sem operação destrutiva. Continua persistindo APENAS metadados/hashes.

-- automated_fixture_validated: indica que a FORMA da assinatura foi validada por fixtures/mocks controlados
-- (sem Enterprise Vault real). Registrada SEPARADAMENTE de runtime_observed / official_documentation /
-- laboratory_validated. NUNCA se grava laboratory_validated = 1 sem prova de laboratório; a validação por
-- fixtures não é laboratório. NULL quando a avaliação não carrega maturidade.
ALTER TABLE dbo.ev_adapter_evaluations ADD
    automated_fixture_validated BIT NULL;
GO
