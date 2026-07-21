# ADR-0007 — Graph Mailbox Import/Export como adapter condicional; rota PST/EV → FTS não habilitada no release inicial

- **Status:** aceito pelo Decision Owner em 2026-07-21; vigência no
  repositório a partir do merge do PR #9.
- **Data:** 2026-07-20 (proposto) · 2026-07-21 (aceito pelo Decision Owner)
- **Gate de aprovação:** Decision Owner + revisão Segurança/Arquitetura
- **Substitui / substituído por:** —

## Registro de aceitação

- **Decision Owner:** Vinicius Miranda — **decisão de aceitação em
  2026-07-21**, após revisão da evidência ao longo de sucessivas iterações.
  **Vigência/publicação no repositório:** a partir do **merge do PR #9**.
- **Fluxo de fechamento (dois PRs):** a evidência foi anexada e mergeada
  pelo **PR #8** (ADR então `proposto`); o **flip para `aceito`** ocorre
  neste **PR #9** — modalidade de PR de aceite separado prevista na
  [matriz](gate-closure-matrix.md), com a evidência já no `main`.
- **Revisão executada (Segurança/Arquitetura):** análise documental
  revalidada em 2026-07-21 em
  [`evidence/0007-evidencia-microsoft-bloqueio.md`](evidence/0007-evidencia-microsoft-bloqueio.md)
  — a análise que sustenta a decisão. Não havendo revisor distinto, a
  competência é exercida/aceita pelo Decision Owner (exceção de bootstrap na
  [matriz](gate-closure-matrix.md)).
- **Evidence Owner:** Engenharia ArchiveBridge.
- **Escopo aceito:** o Graph permanece **adapter condicional**
  (`GraphFtsTargetAdapter`, `CONDITIONAL`); **apenas** a rota
  PST/EV → FTS → Graph fica em `GraphFtsImportFromPstEv =
  BLOCKED_PENDING_EVIDENCE`. **Não** é bloqueio global do Graph. A promoção
  futura segue o ciclo definido (config versionada + capability evidence);
  novo ADR só se mudar contrato, segurança ou arquitetura.

> **Escopo do bloqueio.** O Graph Mailbox Import/Export **não** é removido
> nem bloqueado globalmente como destino: permanece um **adapter
> condicional** (`GraphFtsTargetAdapter`, status arquitetural
> `CONDITIONAL`), disponível na arquitetura e selecionável quando as
> capabilities necessárias estiverem certificadas. O que fica **não
> habilitado no release inicial** é especificamente a **rota
> PST/EV → FTS → Graph**, via a capability
> **`GraphFtsImportFromPstEv = BLOCKED_PENDING_EVIDENCE`**. O objetivo é
> evitar que uma limitação atual dessa rota torne o produto
> permanentemente dependente apenas do Purview.

## Rota bloqueada (específica)

```text
PST legado ou exportação do Enterprise Vault
        ↓
conversão PST/EV → FTS
        ↓
Graph Mailbox Import/Export
```

Essa rota permanece **não habilitada no primeiro release** por ausência de
caminho Microsoft documentado (nas fontes analisadas) e de certificação
interna de fidelidade, escala, idempotência, segurança e operação.

## Contexto

A [§28 Adapter Graph Mailbox Import/Export](../runbook/04-parte-iv-destinos-m365.md#28-adapter-graph-mailbox-importexport---trilha-estratégica-bloqueada) descreve a API e explica em [§28.2](../runbook/04-parte-iv-destinos-m365.md#282-por-que-não-é-o-adapter-ga-do-pst) por que ela **não é** o adapter GA do PST: a importação recebe `Data` em FTS (não MIME/MSG/PST); o cenário documentado é reimportar dados exportados pela própria família de APIs; o tratamento de archives/redirects tem divergência de documentação; throttling/limites e o consentimento privilegiado são preocupações a tratar. A [§28.3](../runbook/04-parte-iv-destinos-m365.md#283-gate-de-habilitação) lista as condições de habilitação; a [§28.4](../runbook/04-parte-iv-destinos-m365.md#284-esqueleto-do-adapter-bloqueado) mostra o esqueleto do adapter que compila mas permanece bloqueado pelo capability gate. Ambientes reais exigem que o produto não fique preso a um único destino — daí o adapter condicional.

## Decisão

1. **Adapter condicional, não removido.** O Graph é registrado no
   [catálogo de adapters de destino](target-adapter-catalog.md) como
   `GraphFtsTargetAdapter`, com **status arquitetural `CONDITIONAL`** e
   **status para PST/EV `BLOCKED_PENDING_EVIDENCE`**. Ele existe na
   arquitetura e é selecionável quando (e apenas quando) suas capabilities
   estiverem certificadas.

2. **Design prospectivo do ingestor.** O design **prevê** que o
   `GraphFtsTargetIngestor` seja implementado como adapter compilável,
   porém bloqueado. **Quando implementado**, o capability gate **deverá
   retornar** um bloqueio **associado explicitamente à capability da rota**,
   impedindo seu uso para migração PST/Enterprise Vault — sem afetar outras
   capabilities futuras do Graph:

   ```text
   Blocked(
       capability: "GraphFtsImportFromPstEv",
       reasonCode: "GRAPH_FTS_PST_EV_PENDING_EVIDENCE"
   )
   ```

   _(Ainda não existe código de produto; esta é uma prescrição de design,
   não um estado atual. O reason code é específico da rota PST/EV — não um
   `GRAPH_FTS_ARCHIVE_NOT_APPROVED` global.)_

3. **Capability específica, não global.** A rota é controlada pela
   capability **`GraphFtsImportFromPstEv`**, com estado inicial
   **`BLOCKED_PENDING_EVIDENCE`** — nome específico para não transmitir que
   todo uso de Graph FTS/archive esteja bloqueado.

4. **Purview permanece o adapter GA inicial planejado para PST**
   (`PurviewPstImportAdapter`, papel `PRIMARY_GA_TARGET`, estado-alvo
   `ENABLED`; [ADR-0006](0006-purview-adapter-ga-inicial.md)). Não está
   habilitado em produção hoje — não há implementação e o ADR-0006 segue
   `proposto` (ver [catálogo](target-adapter-catalog.md)). O condicional do
   Graph não altera essa decisão; apenas garante um segundo caminho
   evoluível.

## Precedência sobre o runbook

Este ADR **substitui, até a consolidação do runbook v1.1, qualquer
interpretação da seção 28 que trate o Graph Mailbox Import/Export como
globalmente bloqueado**. O bloqueio vigente é **exclusivamente** da
capability `GraphFtsImportFromPstEv` (rota PST/EV → FTS → Graph). O DOCX
fonte **não** é alterado agora; registra-se aqui apenas a precedência da
decisão aceita, para evitar interpretações divergentes entre
desenvolvedores.

### Ciclo de promoção da capability

```text
BLOCKED_PENDING_EVIDENCE
→ CANDIDATE
→ CERTIFIED
→ ENABLED
```

A promoção até **`ENABLED`**, quando **todas** as evidências definidas
(§28.3) forem atendidas — incluindo caminho Microsoft documentado para
FTS a partir de PST/EV, corpus de fidelidade, escala, idempotência, análise
de segurança, controle de acesso application-only por **Exchange Online
RBAC for Applications** (papel `Application MailboxItem.ImportExport`, scope
restrito às mailboxes da onda; **substitui** o legado Application Access
Policy), e capability evidence aprovada — ocorre por **configuração
versionada e capability evidence aprovada**. Um **novo ADR** será
necessário **somente** se houver mudança **no contrato, no modelo de
segurança ou na arquitetura** do adapter — **não** para uma simples
promoção já prevista neste ADR.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Habilitar a rota PST/EV → FTS agora | API programática, sem etapas de portal | sem caminho documentado PST/EV → FTS nas fontes analisadas; sem certificação interna | reprovado pela evidência; fail closed |
| Remover/bloquear o Graph globalmente como destino | menos superfície | dependência permanente só do Purview; perde caminho evoluível | contraria o objetivo de não amarrar o produto a um único destino |
| Manter capability global `GraphFtsArchiveImport` | um só nome | transmite que todo Graph FTS/archive está bloqueado | usa-se capability específica da rota (`GraphFtsImportFromPstEv`) |

## Consequências

- Positivas: o produto mantém **dois destinos evoluíveis** (Purview GA hoje;
  Graph condicional com caminho de promoção definido); o bloqueio é preciso
  (rota PST/EV → FTS), não uma exclusão global; promoção prevista não exige
  novo ADR.
- Negativas / dívidas assumidas: quando implementado, o adapter condicional
  compila e permanece bloqueado — exige teste que garanta o estado
  `BLOCKED_PENDING_EVIDENCE`; a divergência de documentação de archives
  exige validação em tenant antes de qualquer promoção.
- Riscos e mitigação: promoção acidental → estado de capability versionado
  + capability evidence obrigatória + teste de precheck assertando o
  bloqueio; leitura de "Graph totalmente bloqueado" → capability específica
  e catálogo deixam o escopo explícito.

## Evidências

Runbook [§28](../runbook/04-parte-iv-destinos-m365.md#28-adapter-graph-mailbox-importexport---trilha-estratégica-bloqueada), [§28.2](../runbook/04-parte-iv-destinos-m365.md#282-por-que-não-é-o-adapter-ga-do-pst), [§28.3](../runbook/04-parte-iv-destinos-m365.md#283-gate-de-habilitação), [§28.4](../runbook/04-parte-iv-destinos-m365.md#284-esqueleto-do-adapter-bloqueado). Referências oficiais: Graph mailbox import/export (concept e v1.0), handle archive mailbox redirects — Apêndice F. Catálogo: [target-adapter-catalog.md](target-adapter-catalog.md). Confirmação do gate: parecer de Segurança/Arquitetura sobre a evidência datada. Promoção futura: ciclo acima (novo ADR só se mudar contrato, segurança ou arquitetura).
