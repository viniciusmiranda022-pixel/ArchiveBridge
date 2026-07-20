# ArchiveBridge — Plataforma de Migração EV/PST → Microsoft 365

Plataforma de migração de arquivos Enterprise Vault e PST legados para o
Online Archive do Microsoft 365, com cadeia de custódia por item, evidência
imutável e reconciliação obrigatória.

> [!CAUTION]
> **BLOQUEIO / DECISÃO CRÍTICA** — o caminho GA (Purview Network Upload)
> limita importação ao mesmo archive a 100 GB. Acima disso o estado é
> `MICROSOFT_ASSESSMENT_REQUIRED`: o produto **bloqueia** o cenário até
> existir conector de ingestão suportado ou parecer formal da Microsoft.
> Auto-expanding archive **não** é bypass do limite do adapter.

**Princípio de projeto:** nenhum item é considerado migrado sem origem
identificada, hash verificável, destino autorizado, resultado persistido e
reconciliação concluída.

## Documento fundador

O repositório é regido pelo **Runbook de Engenharia** (56 páginas,
Confidencial — engenharia e segurança):

| Conteúdo | Onde |
| --- | --- |
| Runbook navegável (Markdown, conversão fiel) | [`docs/runbook/`](docs/runbook/README.md) |
| Originais DOCX/PDF (source of truth) | [`docs/source/`](docs/source/) |
| Relatório e manifesto da conversão | [`docs/runbook/conversion-report.md`](docs/runbook/conversion-report.md) · [`conversion-manifest.json`](docs/runbook/conversion-manifest.json) |
| Conversor determinístico | [`tools/convert_runbook.py`](tools/convert_runbook.py) |
| Testes da conversão | [`tests/test_runbook_conversion.py`](tests/test_runbook_conversion.py) |
| Processo de decisão arquitetural | [`docs/adr/`](docs/adr/README.md) |
| Exportação EV multiversão (revisão arquitetural) | [`docs/ev/`](docs/ev/README.md) |

## Estado do projeto

Fase atual: **documentação e governança** (este baseline). Conforme a
seção 9 do runbook, **nenhum código de produto (.NET) será criado até a
aprovação dos ADRs 0001–0008** em pull request. O scaffolding da seção 10
será executado exatamente uma vez, em repositório limpo, depois disso.

Próximos passos:

1. Ler os capítulos 1 a 5 do runbook e registrar dúvidas como ADR.
2. Aprovar os ADRs obrigatórios — conjunto emendado: **0001–0003,
   0005–0008 e 0013** (o ADR-0004 foi substituído pelo ADR-0013 —
   exportação EV multiversão; ver [`docs/adr/README.md`](docs/adr/README.md)
   e [`docs/ev/`](docs/ev/README.md)).
3. Executar o scaffolding da seção 10.1 e iniciar os épicos da Parte VI.

## Regenerando a documentação

O Markdown de `docs/runbook/` é gerado — não editar manualmente. Alterações
de conteúdo são feitas no DOCX de `docs/source/` e reconvertidas:

```bash
python3 tools/convert_runbook.py          # Linux/macOS
py -3 tools\convert_runbook.py            # Windows
```

Validação (obrigatória antes de commit):

```bash
python3 -m unittest tests.test_runbook_conversion    # Linux/macOS
py -3 -m unittest tests.test_runbook_conversion      # Windows
```

Um workflow de CI (`.github/workflows/ci.yml`) roda esses testes
automaticamente em cada push para `main` e em cada pull request, e falha se
`docs/runbook/` divergir do conversor.

## Governança

- Fluxo exclusivamente por pull request — ver [`CONTRIBUTING.md`](CONTRIBUTING.md).
- Política de segurança e classificação — ver [`SECURITY.md`](SECURITY.md).
- Revisão obrigatória por code owners — ver [`.github/CODEOWNERS`](.github/CODEOWNERS).
- CI automático dos testes de conversão — ver [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
