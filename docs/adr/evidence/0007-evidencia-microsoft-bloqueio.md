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
> API ou o surgimento de um produtor PST/EV → FTS documentado alimentam o
> **ciclo de promoção** da capability `GraphFtsImportFromPstEv`
> (`BLOCKED_PENDING_EVIDENCE → CANDIDATE → CERTIFIED → ENABLED`, ver
> [ADR-0007](../0007-graph-fts-bloqueado.md)); um **novo ADR** só é
> necessário se mudar contrato, segurança ou arquitetura do adapter.

## 1. Perguntas objetivas (dois gates independentes)

"Documentado/suportado pela Microsoft" e "certificado internamente pelo
ArchiveBridge" são **gates diferentes**. A evidência responde a **duas
verificações independentes** sobre a rota PST/EV → FTS → Graph:

- **Q1 (Microsoft):** foi identificado, **nas fontes Microsoft analisadas**,
  um caminho **suportado** para produzir FTS a partir de PST ou export do
  Enterprise Vault? → **Não** (seções 3–4).
- **Q2 (ArchiveBridge):** esse caminho foi **implementado e certificado
  internamente** quanto a fidelidade, escala, idempotência, segurança e
  operação? → **Não** (não implementado nem certificado).

Ambas as respostas são **negativas hoje, por razões diferentes** — o que
sustenta manter a rota em `BLOCKED_PENDING_EVIDENCE` (sem afirmar
inexistência universal nem bloquear o Graph globalmente).

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
2. **A permissão existe.** `MailboxItem.ImportExport.All` está **documentada
   e disponível** (refs. 202–203). Seu caráter altamente privilegiado é
   tratado como **risco de segurança** (seção 5), não como ausência de
   suporte.

### 3.1 Divergência da documentação sobre archive mailboxes

O suporte a **archive mailboxes** **não** é um fato consolidado: há
divergência na própria documentação Microsoft, registrada aqui como
capability a validar (não como suporte integral confirmado):

| Fonte | O que declara |
| --- | --- |
| Página conceitual (ref. 201) | suporte a mailboxes **primary, shared e archive** |
| Visão geral operacional v1.0 (ref. 202) e endpoint v1.0 de mailbox discovery | atualmente listam **somente primary e shared** |
| Tratamento explícito de **auto-expanding archives** (ref. 204) | aparece em documentação **beta** |

**Conclusão de capability:** o suporte operacional completo a archives —
especialmente **auto-expanding archives e redirects** — é uma capability
que **exige validação em tenant** antes de ser declarada; não se afirma
aqui suporte integral a archives como consolidado. Esta divergência **não
altera** a razão principal do bloqueio (seção 4), mas soma-se a ela.

## 4. Por que o ArchiveBridge mantém o bloqueio (escopo específico)

O bloqueio é **específico ao uso do Graph como caminho direto de ingestão
de PST legado ou PST exportado pelo Enterprise Vault** — não ao Graph
Mailbox Import/Export em geral:

1. **O contrato de importação exige FTS.** A operação recebe **FTS em
   Base64**, não PST (ref. 203).
2. **O cenário documentado é outro.** A importação prevê itens compatíveis
   com o formato **produzido por `exportItems`** (round-trip da própria
   família de APIs), não PST/EV (refs. 201, 203).
3. **Nenhum produtor PST/EV → FTS identificado nas fontes analisadas.**
   Não foi identificado, nas fontes oficiais Microsoft analisadas e datadas
   nesta evidência, um caminho documentado para **converter PST ou
   exportação do Enterprise Vault em FTS compatível com a API**.
4. **Sem evidência de fidelidade, escala, idempotência e suporte
   comercial** para essa conversão — condições que a §28.3 exige antes de
   qualquer habilitação.

Ou seja: a superfície é válida; o que **não foi identificado nas fontes
analisadas** é um produtor PST/EV → FTS documentado e suportado. Sem ele, o
adapter não pode ser, hoje, o caminho de migração de PST/EV.

### 4.1 Dois níveis distintos de "suportado"

| Nível | Significado | Situação PST/EV → Graph |
| --- | --- | --- |
| **Caminho oficialmente documentado pela Microsoft** | a Microsoft documenta e suporta a operação | **não identificado nas fontes analisadas** |
| **Implementação certificada internamente pelo ArchiveBridge** | o produto implementa, testa e certifica o caminho (fidelidade, escala, idempotência) | **não implementado nem certificado internamente** |

Enquanto o primeiro nível não for identificado/documentado, o segundo não
se sustenta. O bloqueio decorre da **ausência, nas fontes analisadas**, de
um caminho oficial Microsoft — não de uma afirmação universal de
inexistência.

## 5. Risco de segurança da permissão privilegiada

`MailboxItem.ImportExport.All` é consentimento **application-wide**
altamente privilegiado. Habilitar o adapter sem um produtor FTS suportado
e certificado exporia esse escopo por um fluxo não homologado.

Controle de acesso application-only recomendado quando/se o caminho for
reavaliado: **Exchange Online RBAC for Applications**, com o papel
**`Application MailboxItem.ImportExport`** e **management scope restrito às
mailboxes da onda** — ao invés do mecanismo legado Application Access
Policy, que **não** deve ser adotado em novas implantações. O escopo mínimo
e a capability evidence aprovada permanecem obrigatórios. O caráter
privilegiado é **risco a mitigar**, não prova de ausência de suporte da
API.

## 6. Recomendação

> O Graph Mailbox Import/Export é uma superfície v1.0 válida e permanece um
> **adapter condicional** (`GraphFtsTargetAdapter`, `CONDITIONAL`).
> Entretanto, o ArchiveBridge mantém a rota PST/EV → FTS → Graph
> (`GraphFtsImportFromPstEv`) em **`BLOCKED_PENDING_EVIDENCE`** porque a
> importação exige FTS produzido em formato compatível e **não foi
> identificado, nas fontes Microsoft analisadas e datadas nesta evidência,
> um caminho documentado** para converter PST ou exportação do EV em FTS,
> nem há certificação interna de fidelidade, escala, idempotência,
> segurança e operação. Adicionalmente, o suporte operacional completo a
> archives deverá ser confirmado por validação em tenant diante da
> divergência atual da documentação Microsoft.

A razão registrada do bloqueio é a **ausência, nas fontes analisadas, de um
caminho Microsoft documentado para produzir FTS a partir de PST/EV** (não a
inexistência da API nem bloqueio global do Graph). Some-se a isso a
**divergência de documentação sobre archives** (seção 3.1), que exige
**validação em tenant** — especialmente auto-expanding archives e
redirects. O adapter é **condicional**: a promoção de
`GraphFtsImportFromPstEv` segue o ciclo `BLOCKED_PENDING_EVIDENCE →
CANDIDATE → CERTIFIED → ENABLED` (ver [ADR-0007](../0007-graph-fts-bloqueado.md)
e [catálogo](../target-adapter-catalog.md)); um **novo ADR** só é
necessário se mudar contrato, segurança ou arquitetura. A **aceitação
formal** deste ADR é ato do Decision Owner (Vinicius Miranda) e efetiva-se
com o merge do PR.
