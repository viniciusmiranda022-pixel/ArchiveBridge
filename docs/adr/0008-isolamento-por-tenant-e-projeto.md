# ADR-0008 — Modelo de isolamento por tenant/projeto

- **Status:** proposto
- **Data:** 2026-07-20
- **Gate de aprovação:** segurança e DPO
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** —

## Contexto

A [§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco) exige `tenant_id` em todas as tabelas e índices que começam por tenant nos acessos multitenant. O [§30 Threat model](../runbook/05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos) lista "Cross-tenant access" com controle obrigatório `tenant key, RLS, authorization tests`. A [§31 Identidade e segregação de funções](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções) define personas/workloads com Managed Identity separada e **sem secret compartilhado**. A [§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência) proíbe um módulo referenciar a infraestrutura de outro.

## Decisão

Isolamento **por tenant e por projeto** aplicado em camadas:

- **Dados:** `tenant_id` em todas as tabelas, índices liderados por tenant, **Row-Level Security** e **testes de autorização** que falham o build ao vazar dados entre tenants.
- **Identidade:** uma Managed Identity por workload (PST/Upload/Recon/Evidence/Connector), **sem secret compartilhado**; RBAC mínimo por função (segregação de funções da §31 — quem prepara não aprova, quem aprova não altera artefato).
- **Rede:** NSGs negando tráfego lateral, private endpoints, sem IP público (§7.2).
- **Dados mínimos:** o plano de controle guarda apenas metadados; conteúdo permanece nos artefatos protegidos. Fail closed na ausência de identidade/consentimento/autorização.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Deployment single-tenant por cliente | isolamento físico forte | custo e sobrecarga operacional altos por cliente | inviável como padrão multitenant |
| Filtragem só na aplicação (sem RLS) | simples | uma falha de query vaza dados entre tenants | §30 exige RLS + authorization tests |
| Identidades/segredos compartilhados entre workloads | menos configuração | quebra segregação de funções e princípio de menor privilégio | proibido pela §31 |

## Consequências

- Positivas: garantia forte contra acesso cross-tenant; menor privilégio por workload; blast radius reduzido.
- Negativas / dívidas assumidas: maior complexidade de identidade/infra e de testes de autorização; RLS exige disciplina de esquema.
- Riscos e mitigação: regressão de isolamento → testes de autorização e de arquitetura no CI; exposição de dados pessoais → revisão do DPO e minimização de dados.

## Evidências

Runbook [§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco), [§30](../runbook/05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos), [§31](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções), [§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência). Confirmação do gate: parecer de segurança e do DPO anexados ao PR.
