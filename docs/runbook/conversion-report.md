# Relatório de Conversão — Runbook de Engenharia DOCX → Markdown

- **Data da conversão:** 2026-07-20
- **Conversor:** `tools/convert_runbook.py` (Python, somente stdlib, determinístico)
- **Validação:** `tests/test_runbook_conversion.py` — 16 testes, todos verdes
- **Manifesto de auditoria:** [`conversion-manifest.json`](conversion-manifest.json)

## Arquivos-fonte (source of truth)

| Arquivo | SHA-256 |
| --- | --- |
| `docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx` | `c84d1825e0faa2b1ad2c5891f67045a7487523bc484967c460988c93cec3c5b4` |
| `docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.pdf` | `863a91e70d5874803ad0b57fc40d7ac734e99823232c4e5c0912614785a81251` |

O PDF (56 páginas) foi usado como referência de renderização; a conversão
lê exclusivamente o DOCX.

## Contagens — origem vs. geradas

Contagens de origem derivadas do `conversion-manifest.json` (body blocks =
`w:p` + `w:tbl`; elementos editoriais do conversor são marcados
`sourceType: "generated"` e excluídos destas contagens).

| Elemento | DOCX (análise independente) | Convertido | Status |
| --- | --- | --- | --- |
| Body blocks | 1026 (999 parágrafos + 27 tabelas) | 1026 | ✅ 992 convertidos + 34 ignorados justificados |
| Heading1 | 13 | 13 | ✅ todos roteados via `OUTPUT_ROUTING` |
| Heading2 | 51 | 51 | ✅ |
| Heading3 | 115 | 115 | ✅ |
| Itens de lista (bullet) | 414 | 414 | ✅ |
| Itens de lista (numerados) | 215 | 215 | ✅ sequência contínua 1–215 |
| Tabelas | 27 | 27 | ✅ todas simples → GFM (fallback HTML não foi necessário) |
| Blocos de código | 41 | 41 | ✅ rótulos: TEXT×8, POWERSHELL×14, CSHARP×7, JSON×6, SQL×3, HTTP×1, CSV×1, BICEP×1 |
| Caixas de destaque | 12 | 12 | ✅ convertidas em GitHub alerts (ver mapeamento abaixo) |
| Diagramas | 3 | 3 | ✅ extraídos por relacionamento (`r:embed` → rels → media) |
| Hyperlinks externos | 20 | 20 | ✅ como links Markdown |

**Numeração contínua verificada contra o PDF**: o estilo `ListNumber`
referencia um único `numId` sem restart; a renderização oficial confirma a
sequência atravessando capítulos (ex.: 1–5 no "Como usar", 36–45 na seção
17.1, 196–215 no Apêndice F). O Markdown reproduz exatamente esses números.

## Exceções e elementos não representáveis

| Elemento | Tratamento |
| --- | --- |
| 34 parágrafos vazios (espaçamento visual) | Ignorados com justificativa individual no manifesto (`status: "ignored"`) |
| Cabeçalho/rodapé de página do DOCX (título, classificação, nº de página) | Não convertidos: são chrome de paginação, não conteúdo do corpo; a classificação aparece na capa (`00-mapa-do-documento.md`) e em `SECURITY.md` |
| Thumbnail embutido do DOCX (`docProps/thumbnail.jpeg`) | Não convertido: artefato de pré-visualização do Word |
| Caixas coloridas de destaque | Mapeadas para GitHub alerts preservando o texto integral: `FDECEC`→`[!CAUTION]`, `FFF4D6`→`[!WARNING]`, `E8F1F8`→`[!NOTE]`, `EAF6ED`→`[!TIP]`, `F2F4F7`→`[!IMPORTANT]` |
| Rótulos de linguagem dos blocos de código (ex.: `POWERSHELL`) | Removidos do conteúdo e usados como linguagem do fence |
| Placeholders `<…>` em texto corrido | Escapados (`\<…\>`) para o GFM não interpretá-los como HTML |

## Spot-checks manuais

| Seção | Verificação | Resultado |
| --- | --- | --- |
| 8 — Estrutura do repositório | Árvore `/src…` íntegra em fence `text`, indentação preservada | ✅ |
| 16.3 — Export EV em partes de 18 GiB | Script PowerShell completo, continuações de linha (`` ` ``) preservadas, `-MaxPSTSizeMB` 500–51200 e default 18432 corretos | ✅ |
| 25.8 — CSV mapping oficial | Cabeçalho de 10 colunas idêntico ao original, exemplos e validações do builder completos | ✅ |
| Apêndice A — DDL de referência | DDL em fence `sql`, tabelas/constraints íntegras | ✅ |
| 17.1 / Apêndice F — numeração contínua | 36–45 e 196–215 idênticos ao PDF | ✅ |

## PRE-PUSH SECURITY GATE

Executado em 2026-07-20 contra `viniciusmiranda022-pixel/ArchiveBridge`
(API GitHub autenticada):

| Controle | Resultado | Evidência |
| --- | --- | --- |
| Repositório privado | **FAIL** | `visibility: public`, `private: false` |
| Acesso restrito à equipe autorizada | **FAIL** | leitura pública; colaborador direto: apenas o owner (admin) |
| GitHub Pages não habilitado | PASS | `has_pages: false` |
| Fork público não permitido | **FAIL** | `allow_forking: true` em repositório público |
| Actions/apps sem acesso irrestrito | PASS | 0 workflows, repositório sem histórico |
| Remoto correto (ArchiveBridge, nunca PAM) | PASS | `git remote -v` verificado na sessão |

**Disposição:** pela regra fail-closed, o push estava bloqueado. O owner
do repositório (@viniciusmiranda022-pixel) foi consultado e **autorizou
formalmente, em 2026-07-20, a publicação do runbook neste repositório
público**, ciente da classificação Confidencial do documento. Esta
autorização está registrada aqui e na descrição do pull request de
baseline. Recomendação permanente: tornar o repositório privado e
desabilitar forks enquanto a classificação do documento não mudar.
