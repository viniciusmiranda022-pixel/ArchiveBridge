# Contribuindo com o ArchiveBridge

## Fluxo exclusivamente por pull request

Conforme o runbook (seção "Como o desenvolvedor deve usar este runbook" e
seção 10.3), **toda alteração passa por pull request** — nunca commit
direto em `main`. Cada PR exige:

1. Branch descritivo a partir de `main`.
2. Testes verdes (`python3 -m unittest` / `py -3 -m unittest`).
3. Revisão de code owner (ver `.github/CODEOWNERS`).
4. Nenhum segredo ou identificador real (ver `SECURITY.md`).

## Ordem de trabalho obrigatória

O runbook define a sequência e ela não é negociável:

1. **ADRs antes de código** — os ADRs 0001–0008 (`docs/adr/README.md`)
   precisam estar aprovados em PR antes de qualquer projeto .NET.
2. **Scaffolding exatamente uma vez** — a sequência da seção 10.1 roda uma
   única vez em repositório limpo; depois, só PR.
3. **Vertical slices** — implementar por fatia vertical e liberar somente
   com o Definition of Done da etapa integralmente verde.
4. **Divergência da Microsoft bloqueia** — quando a documentação oficial
   divergir do runbook, bloquear a feature, registrar ADR e atualizar a
   matriz de capacidades (seção 3).

## Editando a documentação do runbook

`docs/runbook/*.md` (exceto `conversion-report.md`) é **gerado** por
`tools/convert_runbook.py` a partir do DOCX em `docs/source/`. Não edite os
.md manualmente:

1. Altere o DOCX (nova versão do documento).
2. Rode `python3 tools/convert_runbook.py`.
3. Rode `python3 -m unittest tests.test_runbook_conversion` — os testes
   comparam byte a byte a saída commitada com uma reconversão limpa e
   auditam contagens via `conversion-manifest.json`.
4. Atualize `docs/runbook/conversion-report.md` (hashes e contagens) no
   mesmo PR.

## ADRs

Use `docs/adr/0000-template.md`. Numeração sequencial (`NNNN-titulo.md`),
um ADR por decisão, aprovação registrada no PR pelo gate indicado na
tabela da seção 9 do runbook.
