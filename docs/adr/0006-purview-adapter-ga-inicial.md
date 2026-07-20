# ADR-0006 — Purview Network Upload como adapter GA inicial

- **Status:** proposto
- **Data:** 2026-07-20
- **Gate de aprovação:** evidência oficial e teste em tenant controlado
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** —

## Contexto

A [§24 Strategy e capability gates](../runbook/04-parte-iv-destinos-m365.md#24-strategy-e-capability-gates) estabelece que "Microsoft 365" não é uma única capacidade: cada destino é avaliado por `ITargetIngestor` antes de aceitar uma onda. A [§25 Adapter Purview Network Upload - caminho GA](../runbook/04-parte-iv-destinos-m365.md#25-adapter-purview-network-upload---caminho-ga) define o Purview Network Upload (AzCopy + CSV mapping oficial) como o adapter habilitado no primeiro release, que **prepara e transporta**, enquanto a criação/início do job permanece no portal Purview. O bloqueio de >100 GB no mesmo archive (`MICROSOFT_ASSESSMENT_REQUIRED`) é mantido.

## Decisão

**Purview Network Upload** é o **único adapter de destino habilitado no primeiro release**, atrás de `ITargetIngestor`. O produto prepara parts, gera o CSV mapping oficial e transporta via AzCopy homologado; a **criação e o início do import job permanecem como tarefa de workflow humana no portal Purview** (§25.9), conforme orientação da Microsoft. O capacity gate (§25.4) é obrigatório e **bloqueia >100 GB para o mesmo archive** com estado `MICROSOFT_ASSESSMENT_REQUIRED`; auto-expanding archive não é bypass do limite do adapter.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Graph Mailbox Import/Export (FTS) | API programática | não recebe PST; não-GA para o cenário | bloqueado (ADR-0007) |
| Adapter contratual/partner de ingestão rápida | possível throughput | exige contrato/SDK e projeto separado (§29) | fora do primeiro release |
| Criar/iniciar o job via API em vez do portal | menos passos manuais | não suportado pela orientação atual da Microsoft | fail-closed: só caminho suportado |

## Consequências

- Positivas: caminho GA/suportado, com evidência oficial; menor risco regulatório e de suporte.
- Negativas / dívidas assumidas: etapas humanas no portal (workflow, não cliques fora do sistema); dependência do staging Microsoft (§25.10).
- Riscos e mitigação: mudança na documentação Microsoft → bloquear feature e registrar ADR (§2/§3); cenário >100 GB → pacote para suporte Microsoft e estado `WAITING_EXTERNAL` (§27).

## Evidências

Runbook [§24](../runbook/04-parte-iv-destinos-m365.md#24-strategy-e-capability-gates), [§25](../runbook/04-parte-iv-destinos-m365.md#25-adapter-purview-network-upload---caminho-ga), [§27](../runbook/04-parte-iv-destinos-m365.md#27-cenários-acima-de-100-gb-no-mesmo-archive). Referências oficiais: PST Import overview / Network upload / Troubleshooting / FAQ — Apêndice F. Confirmação do gate: evidência oficial citada + resultado de teste em tenant controlado anexados ao PR.
