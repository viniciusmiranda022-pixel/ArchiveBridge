# ADR-0007 — Graph Mailbox Import/Export (FTS) bloqueado

- **Status:** proposto
- **Data:** 2026-07-20
- **Gate de aprovação:** reavaliação quando archive/FTS estiverem suportados
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** —

## Contexto

A [§28 Adapter Graph Mailbox Import/Export - trilha estratégica bloqueada](../runbook/04-parte-iv-destinos-m365.md#28-adapter-graph-mailbox-importexport---trilha-estratégica-bloqueada) descreve a API e explica em [§28.2](../runbook/04-parte-iv-destinos-m365.md#282-por-que-não-é-o-adapter-ga-do-pst) por que ela **não é** o adapter GA do PST: não recebe PST; o `Data` é FTS (não MIME/MSG/PST); o cenário documentado é reimportar dados exportados pela própria família de APIs; archive discovery/redirect ainda precisa de homologação; throttling/limites e consentimento application-wide são preocupações abertas. A [§28.3](../runbook/04-parte-iv-destinos-m365.md#283-gate-de-habilitação) lista as condições de habilitação; a [§28.4](../runbook/04-parte-iv-destinos-m365.md#284-esqueleto-do-adapter-bloqueado) mostra o adapter que compila mas permanece bloqueado pelo capability gate.

## Decisão

O `GraphFtsTargetIngestor` é implementado como **adapter compilável, porém bloqueado**: o capability gate retorna `Blocked("GRAPH_FTS_ARCHIVE_NOT_APPROVED")` e o adapter **não é usado** para migração de PST. A habilitação só ocorre quando **todas** as condições da §28.3 forem verdadeiras (API v1.0 GA para o cenário/cloud, descoberta de archive documentada e testada, confirmação Microsoft de que FTS a partir de PST/EV é suportado, corpus de fidelidade + análise de segurança do codec FTS, throttling/custo conhecidos, Application Access Policy, consentimento aprovado, tratamento de redirect 308/409, pen-test e load test, e capability evidence aprovada pelo Change Advisory Board).

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Habilitar Graph FTS agora | API programática, sem etapas de portal | não-GA para o cenário; não aceita PST; FTS ≠ MIME/MSG | reprovado pela §28.2 |
| Remover o adapter do código | menos superfície | perde a costura arquitetural e o gate explícito | mantê-lo bloqueado documenta o caminho futuro sem expô-lo |

## Consequências

- Positivas: mantém a costura de `ITargetIngestor` e um gate explícito, sem expor caminho não suportado; gatilho de reavaliação definido.
- Negativas / dívidas assumidas: código morto controlado (compila, não executa) exige teste que garanta que permanece bloqueado.
- Riscos e mitigação: habilitação acidental → teste de precheck assertando `Blocked`; mudança na Microsoft → reavaliação formal e novo ADR que substitua este.

## Evidências

Runbook [§28](../runbook/04-parte-iv-destinos-m365.md#28-adapter-graph-mailbox-importexport---trilha-estratégica-bloqueada), [§28.2](../runbook/04-parte-iv-destinos-m365.md#282-por-que-não-é-o-adapter-ga-do-pst), [§28.3](../runbook/04-parte-iv-destinos-m365.md#283-gate-de-habilitação), [§28.4](../runbook/04-parte-iv-destinos-m365.md#284-esqueleto-do-adapter-bloqueado). Referências oficiais: Graph mailbox import/export (concept e v1.0), handle archive mailbox redirects — Apêndice F. Confirmação do gate: reavaliação registrada em novo ADR quando as condições da §28.3 forem atendidas.
