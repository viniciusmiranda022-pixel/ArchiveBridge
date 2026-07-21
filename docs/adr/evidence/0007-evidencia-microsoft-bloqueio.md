# Evidência do bloqueio do Graph FTS para PST/EV — gate do ADR-0007

Evidência requerida pelo gate do
[ADR-0007](../0007-graph-fts-bloqueado.md) (Graph Mailbox Import/Export /
FTS mantido bloqueado **como adapter de migração PST/Enterprise Vault** no
primeiro release).

- **Tipo:** análise da documentação oficial Microsoft (revisão
  Segurança/Arquitetura)
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge, sob direção
  do Decision Owner
- **Data da análise / revalidação documental:** 2026-07-21
- **Natureza:** a análise **sustenta a decisão de manter o bloqueio do
  caminho PST/EV → Graph**; não é a aceitação formal (ato do Decision
  Owner — ver "Recomendação").

> [!NOTE]
> Análise documental **datada (revalidada em 2026-07-21)**. A evolução da
> API ou o surgimento de um produtor PST/EV → FTS oficialmente suportado
> são gatilho para um **novo ADR substituto** (condições da §28.3), não
> para a reabertura deste.

## 1. Pergunta objetiva

Existe caminho **oficialmente documentado e certificado** para usar a API
Graph Mailbox Import/Export como adapter de **migração de PST legado ou de
export do Enterprise Vault** para o Microsoft 365, com fidelidade e suporte
adequados?

## 2. Fontes analisadas (Apêndice F)

| Ref. | Documento |
| --- | --- |
| 201 | [Graph mailbox import/export — concept](https://learn.microsoft.com/en-us/graph/mailbox-import-export-concept-overview) |
| 202 | [Graph mailbox import/export — API v1.0](https://learn.microsoft.com/en-us/graph/api/resources/mailbox-import-export-api-overview?view=graph-rest-1.0) |
| 203 | [Import Exchange mailbox item com FTS](https://learn.microsoft.com/en-us/graph/import-exchange-mailbox-item) |
| 204 | [Archive mailbox redirects](https://learn.microsoft.com/en-us/graph/handle-archive-mailbox-redirects) |
| 205 | [EWS deprecation](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-ews-exchange-online) |

## 3. O que a documentação atual **suporta** (não é objeto de bloqueio)

Registrado para não incorrer em subafirmação — o Graph Mailbox
Import/Export é uma superfície válida:

1. **É v1.0.** `createImportSession` e `exportItems` possuem documentação
   em Microsoft Graph **v1.0** (refs. 201–203).
2. **Cobre archive mailboxes.** A documentação declara suporte a mailboxes
   **primary, shared e archive** (refs. 201–202). Não se afirma aqui que o
   suporte a archive esteja em beta.
3. **A permissão existe.** `MailboxItem.ImportExport.All` está **documentada
   e disponível** (refs. 202–203). Seu caráter altamente privilegiado é
   tratado como **risco de segurança** (seção 5), não como ausência de
   suporte.
4. **Redirects de auto-expanding archive** (ref. 204) podem envolver
   superfícies ainda em evolução, mas isso **não** permite generalizar que
   todo o suporte a archive seja beta.

## 4. Por que o ArchiveBridge mantém o bloqueio (escopo específico)

O bloqueio é **específico ao uso do Graph como caminho direto de ingestão
de PST legado ou PST exportado pelo Enterprise Vault** — não ao Graph
Mailbox Import/Export em geral:

1. **O contrato de importação exige FTS.** A operação recebe **FTS em
   Base64**, não PST (ref. 203).
2. **O cenário documentado é outro.** A importação prevê itens compatíveis
   com o formato **produzido por `exportItems`** (round-trip da própria
   família de APIs), não PST/EV (refs. 201, 203).
3. **Não há produtor oficial PST/EV → FTS.** Não existe mecanismo Microsoft
   documentado para **converter PST ou itens do Enterprise Vault em FTS
   suportado**.
4. **Sem evidência de fidelidade, escala, idempotência e suporte
   comercial** para essa conversão — condições que a §28.3 exige antes de
   qualquer habilitação.

Ou seja: a superfície é válida; o que falta é o **produtor PST/EV → FTS
oficialmente suportado e certificado**. Sem ele, o adapter não pode ser o
caminho GA de migração de PST/EV.

## 5. Risco de segurança da permissão privilegiada

`MailboxItem.ImportExport.All` é consentimento **application-wide**
altamente privilegiado. Habilitar o adapter sem um produtor FTS suportado
e certificado exporia esse escopo por um fluxo não homologado — risco
tratado como controle de segurança (Application Access Policy, escopo
mínimo, capability evidence aprovada) quando/se o caminho for reavaliado.
O caráter privilegiado é **risco a mitigar**, não prova de ausência de
suporte da API.

## 6. Recomendação

> O Microsoft Graph Mailbox Import/Export é uma superfície v1.0 válida e
> oferece suporte a archive mailboxes. Entretanto, o ArchiveBridge mantém
> bloqueado o uso dessa API como adapter para migração PST/Enterprise
> Vault, pois o contrato de importação exige FTS e não existe caminho
> oficial documentado e certificado para converter PST ou exportação do EV
> em FTS com fidelidade e suporte adequados.

A capability `GraphFtsArchiveImport` permanece **`BLOCKED`**. A razão
registrada do bloqueio é a **ausência de um produtor PST/EV → FTS
oficialmente suportado e certificado** (não a inexistência da API nem
suporte a archive). A **aceitação formal** é ato do Decision Owner
(Vinicius Miranda) e efetiva-se com o merge do PR que anexa esta evidência
e altera o status do ADR-0007 para `aceito`. Reavaliação futura ocorre por
**novo ADR substituto** (§28.3).
