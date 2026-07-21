# Evidência Microsoft do bloqueio do Graph FTS — gate do ADR-0007

Evidência requerida pelo gate do
[ADR-0007](../0007-graph-fts-bloqueado.md) (Graph Mailbox Import/Export /
FTS mantido bloqueado no primeiro release).

- **Tipo:** análise da documentação oficial Microsoft (revisão
  Segurança/Arquitetura)
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge, sob direção
  do Decision Owner
- **Data da análise:** 2026-07-21 · **Documentação de referência:** estado
  citado no runbook (Apêndice F, refs. 201–205), consolidado em 2026-07-20
- **Natureza:** a análise **sustenta a decisão de manter o bloqueio**; não
  é a aceitação formal (ato do Decision Owner — ver "Recomendação").

> [!NOTE]
> Esta é uma análise documental datada. A disponibilidade futura do Graph
> FTS **não** reabre este ADR — ela é gatilho para um **novo ADR
> substituto** (condições da §28.3). A evidência deve ser revalidada se e
> quando esse novo ADR for aberto.

## 1. Pergunta objetiva

A documentação oficial atual autoriza usar a API Graph Mailbox
Import/Export (FTS) como caminho de **importação de PST para o Online
Archive** do Microsoft 365, em v1.0 GA, para o cenário deste produto?

## 2. Fontes analisadas (Apêndice F)

| Ref. | Documento | O que estabelece |
| --- | --- | --- |
| 201 | [Graph mailbox import/export — concept](https://learn.microsoft.com/en-us/graph/mailbox-import-export-concept-overview) | visão conceitual; cenário-alvo é reimportar dados **exportados pela própria família de APIs**, em formato FTS |
| 202 | [Graph mailbox import/export — API v1.0](https://learn.microsoft.com/en-us/graph/api/resources/mailbox-import-export-api-overview?view=graph-rest-1.0) | superfície v1.0; recursos e escopo publicados |
| 203 | [Import Exchange mailbox item com FTS](https://learn.microsoft.com/en-us/graph/import-exchange-mailbox-item) | o payload de importação é **FTS (Fast Transfer Stream)**, não MIME/MSG/PST |
| 204 | [Archive mailbox redirects](https://learn.microsoft.com/en-us/graph/handle-archive-mailbox-redirects) | descoberta/redirect de archive (308, `ErrorArchiveFolderMovedPermanently`) documentada em superfície ainda de amadurecimento |
| 205 | [EWS deprecation](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-ews-exchange-online) | EWS em depreciação — não é alternativa de longo prazo |

## 3. Análise

Os fatos documentados sustentam, cada um de forma independente, que o
cenário PST→archive **não está aprovado** por esta API hoje (§28.2 do
runbook):

1. **A API não recebe PST.** O item importado é FTS (ref. 203). Usar a API
   para PSTs exigiria implementar/usar um produtor de Fast Transfer Stream
   completo e **demonstrar suporte** — o que a documentação não confere.
2. **O cenário documentado é outro.** A concepção (ref. 201) descreve
   reimportar dados **exportados pela mesma família de APIs**, não ingerir
   PST legado ou export de Enterprise Vault.
3. **Descoberta de archive ainda amadurecendo.** O tratamento de
   redirects de archive (ref. 204) não está consolidado para o fluxo
   pretendido; a §28.1 registra que páginas de redirect usam superfícies
   beta.
4. **Sem garantia de escala/limites.** A documentação não fixa
   throttling/limites adequados a centenas de milhões de itens (§28.2);
   consentimento application-wide é altamente privilegiado (risco de
   segurança).
5. **EWS não é rota alternativa.** Está em depreciação (ref. 205).

Nenhuma das fontes autoriza o cenário; várias o contraindicam. Não foi
encontrada evidência oficial que **habilite** o uso pretendido em v1.0 GA.

## 4. Risco de segurança de não bloquear

Habilitar o adapter sem suporte oficial exigiria consentimento
application-wide de `MailboxItem.ImportExport(.All)` — permissão altamente
privilegiada — para um fluxo não suportado e não testado em fidelidade.
Manter a capability `BLOCKED` (fail closed) é a postura correta de
segurança até que **todas** as condições da §28.3 sejam satisfeitas e
registradas em capability evidence aprovada.

## 5. Recomendação

A documentação oficial **sustenta o bloqueio**: recomenda-se aceitar a
decisão do ADR-0007 de manter o Graph FTS bloqueado e fora do primeiro
release, com a capability `GraphFtsArchiveImport` em `BLOCKED`.

A **aceitação formal** é ato do Decision Owner (Vinicius Miranda) e
**efetiva-se com o merge** do PR que anexa esta evidência e altera o status
do ADR-0007 para `aceito`. A reavaliação futura, se as condições da §28.3
mudarem, ocorre por **novo ADR substituto**, não pela reabertura deste.
