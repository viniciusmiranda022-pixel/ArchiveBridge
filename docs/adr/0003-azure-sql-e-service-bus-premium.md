# ADR-0003 — Azure SQL e Service Bus Premium

- **Status:** proposto
- **Data:** 2026-07-20
- **Gate de aprovação:** arquitetura + FinOps
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** —

## Contexto

A [§12 Persistência, concorrência e esquema de banco](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco) define Azure SQL como sistema de registro do controle, com `rowversion` para concorrência otimista e `tenant_id` em todas as tabelas. A [§14 Mensageria e execução durável](../runbook/02-parte-ii-arquitetura.md#14-mensageria-e-execução-durável) exige Service Bus Premium por isolamento, sessions, duplicate detection e DLQ, transportando apenas comandos pequenos. O princípio "at-least-once técnico, exactly-once de efeito" (§6) sustenta a escolha via Outbox/Inbox.

## Decisão

Usar **Azure SQL** como sistema de registro transacional do plano de controle (estado, locks, outbox/inbox), com concorrência otimista por `rowversion` e `tenant_id` em todas as tabelas. Usar **Azure Service Bus Premium** para comandos assíncronos e eventos, com sessions, duplicate detection e dead-letter. A entrega é **at-least-once** e o efeito é **exactly-once** via Outbox/Inbox, unique constraints e idempotency key. Blob é o sistema de registro dos artefatos; **as filas nunca transportam conteúdo de e-mail nem SAS** — apenas IDs e contexto mínimo.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| PostgreSQL / Cosmos DB | custo/flexibilidade | menos aderência a rowversion/RLS e ao ferramental EF Core alvo | runbook fixa Azure SQL como registro do controle |
| Service Bus Standard | mais barato | sem isolamento dedicado nem recursos Premium | §14 exige isolamento, sessions e duplicate detection |
| Kafka / RabbitMQ | ecossistema | operação adicional, sem integração nativa Azure/DLQ equivalente | overhead operacional sem ganho no cenário |
| Fila baseada só no banco | menos serviços | acopla throughput de fila ao SQL; sem DLQ nativo | perde durabilidade/isolamento da mensageria |

## Consequências

- Positivas: HA gerenciada; throughput e isolamento previsíveis; padrão outbox/inbox garante idempotência.
- Negativas / dívidas assumidas: custo do namespace Premium e do tier de SQL — foco do gate FinOps; sizing por ambiente.
- Riscos e mitigação: custo acima do previsto → dimensionamento por ambiente e revisão FinOps; poison message → schema + inbox + DLQ (§14.4).

## Evidências

Runbook [§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco), [§14](../runbook/02-parte-ii-arquitetura.md#14-mensageria-e-execução-durável) e Apêndice A (DDL). Referência oficial: Azure Service Bus (Well-Architected) — Apêndice F. Confirmação do gate: parecer de arquitetura e FinOps no PR, com estimativa de custo por ambiente.
