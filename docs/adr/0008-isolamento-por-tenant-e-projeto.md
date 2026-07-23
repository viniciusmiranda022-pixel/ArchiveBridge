# ADR-0008 — Modelo de isolamento por tenant/projeto (identidade, segredos e rede on-premises)

- **Status:** proposto
- **Data:** 2026-07-20 (versão original) · 2026-07-23 (reescrito — alinhamento on-premises)
- **Decision Owner:** Vinicius Miranda (aceitação formal pendente)
- **Revisor necessário:** Segurança/Privacidade (DPO)
- **Gate de aprovação:** threat model on-premises + avaliação de dados/privacidade, revisados por Segurança/DPO
- **Substitui / substituído por:** reescreve a versão anterior deste ADR (isolamento expresso em primitivos Azure — Managed Identity, private endpoints, NSG), que **não** foi aceita. Nome do arquivo mantido por estabilidade de referências.

## Contexto

A [§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco) exige `tenant_id` em todas as tabelas e índices que começam por tenant nos acessos multitenant. O [§30 Threat model](../runbook/05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos) lista "Cross-tenant access" com controle obrigatório `tenant key, RLS, authorization tests`. A [§31 Identidade e segregação de funções](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções) define personas/workloads com identidade separada e **sem secret compartilhado**. A [§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência) proíbe um módulo referenciar a infraestrutura de outro.

A baseline arquitetural vigente é **on-premises** ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)): identidade, segredos, rede, storage e workers ficam na infraestrutura do cliente; o **Microsoft 365 é destino externo** (saída HTTPS 443). A versão anterior deste ADR expressava o isolamento em primitivos Azure (Managed Identity, Key Vault, private endpoints, NSG). O ADR-0003 registrou, entre suas **pendências downstream**, exatamente a "revisão do ADR-0008 (identidade/rede on-prem)"; este ADR a executa.

## Precedência sobre o runbook

O runbook v1.0 expressa o modelo em PaaS Azure — Managed Identity ([§31](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções)), Key Vault ([§32](../runbook/05-parte-v-seguranca-infra-operacao.md#32-segredos-e-material-criptográfico)), storage account/private endpoint ([§33](../runbook/05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida)), IaC Azure/NSG/private endpoints (§36) e topologia de rede ([§7.2](../runbook/02-parte-ii-arquitetura.md#72-topologia-de-rede)). Conforme o [ADR-0003](0003-azure-sql-e-service-bus-premium.md), esses pressupostos PaaS **não** são a baseline vigente. Este ADR reconcilia o modelo de isolamento com a baseline on-premises, usando o mesmo padrão de emenda já aplicado em ADR-0003/§9, ADR-0007/§9 e ADR-0006. O DOCX fonte **não** é alterado agora; a revisão da Parte V para a v1.1 é ação pendente do owner do documento. **Os objetivos de controle das §30–§35 são preservados integralmente** — muda a *realização técnica*, não o objetivo.

## Decisão

Isolamento **por tenant e por projeto**, realizado com primitivos **on-premises**, em camadas:

### Dados

- `tenant_id` em todas as tabelas; índices liderados por tenant;
- **Row-Level Security do SQL Server** — recurso **nativo do SQL Server, válido on-premises** — mais **testes de autorização** que falham o build ao vazar dados entre tenants. O controle obrigatório da §30 (`tenant key, RLS, authorization tests`) permanece **inalterado**, pois o RLS é do próprio SQL Server (sistema de registro do ADR-0003).

### Identidade

- **uma identidade de serviço Windows por workload** (PST / Upload / Recon / Evidence): **gMSA ou virtual service account**, **nunca Domain Admin** (§34); **sem secret compartilhado** entre workloads; RBAC mínimo por função e **segregação de funções** da §31 (quem prepara não aprova; quem aprova não altera artefato);
- **Source Connector:** certificado por instalação (enrollment mTLS) — já on-premises na §31;
- **Destino Microsoft 365 (externo):** autenticação **app-only por certificado (CBA)** no Microsoft Entra ID, com **role mínimo** (**Exchange Online RBAC for Applications** — [ADR-0007](0007-graph-fts-bloqueado.md)) e management scope restrito às mailboxes da onda. **Managed Identity aplica-se apenas quando um componente estiver de fato hospedado em Azure** — não é a baseline on-premises (§31 já diz "Managed Identity é preferida **em Azure**").

### Segredos e material criptográfico

- custódia pelo **mecanismo de segredos on-premises** ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)): **DPAPI (perfil de nó único)** / **mecanismo de segredo multi-nó (perfil HA)**; **Certificate Store** do Windows para certificados; **ACLs exclusivas**;
- **SAS do Purview** custodiado por esse mecanismo ([ADR-0006](0006-purview-adapter-ga-inicial.md)), com validação de host/HTTPS/container/expiry, tags de wave, leitura restrita à identidade do upload worker e eliminação após o upload;
- **chaves HMAC por tenant, versionadas** (`keyVersion` persistida; rotação **não** invalida fingerprints antigos) — controle de nível de aplicação, inalterado;
- **assinatura do evidence package com chave não exportável** (TPM/HSM local quando a exigência justificar);
- **renovação de certificados** automatizada, com alarmes 30/14/7 dias;
- **redaction central** de toda telemetria ([§32.1](../runbook/05-parte-v-seguranca-infra-operacao.md#321-redaction-central)), com testes de *canary* que falham o build — inalterado.

### Rede

- **nenhuma porta de entrada vinda da internet**; **saída somente HTTPS 443** aos endpoints Microsoft exigidos (Entra ID, Exchange Online, Graph, Purview e o Azure Storage temporário do Purview) — [ADR-0003](0003-azure-sql-e-service-bus-premium.md);
- **comunicação interna restrita por allowlist**, documentada na **matriz de fluxos e portas** (ADR-0003), negando tráfego lateral desnecessário; **segmentação de rede** por firewall/VLAN do cliente;
- os controles Azure de rede (NSG, private endpoints, ausência de IP público) são a **realização específica do perfil Azure opcional**, não a baseline.

### Dados mínimos e fail-closed

- o plano de controle guarda **apenas metadados**; o conteúdo permanece nos artefatos protegidos em storage local/NAS/SMB (ADR-0003), com ciclo de vida por container ([§33](../runbook/05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida)) e hardening dos workers ([§34](../runbook/05-parte-v-seguranca-infra-operacao.md#34-hardening-dos-workers-windows)); malware/conteúdo hostil tratado por [§35](../runbook/05-parte-v-seguranca-infra-operacao.md#35-malware-e-conteúdo-hostil);
- **fail closed** na ausência de identidade, consentimento ou autorização.

## Perfil Azure opcional (não requisito)

Se um cliente específico optar por um perfil de implantação em Azure (ADR-0003, "adapters opcionais futuros"), os controles nativos — Managed Identity, Key Vault (soft delete / purge protection / private endpoint), Storage com private endpoint, NSG e Azure Policy (§32/§33/§36) — passam a ser a **realização daquele perfil** dos mesmos objetivos de controle deste ADR. Eles **não** são dependência obrigatória do produto e **não** alteram a baseline on-premises vigente.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Deployment single-tenant por cliente | isolamento físico forte | custo e sobrecarga operacional altos por cliente | inviável como padrão multitenant |
| Filtragem só na aplicação (sem RLS) | simples | uma falha de query vaza dados entre tenants | §30 exige RLS + authorization tests |
| Identidades/segredos compartilhados entre workloads | menos configuração | quebra segregação de funções e menor privilégio | proibido pela §31 |
| Exigir Managed Identity/Key Vault Azure como mecanismo **obrigatório** | primitivos gerenciados | exige Azure; contraria a baseline on-premises (ADR-0003) | on-premises: gMSA/CBA + DPAPI/multi-nó; Azure apenas como perfil opcional |

## Consequências

- Positivas: garantia forte contra acesso cross-tenant; menor privilégio por workload; blast radius reduzido; **modelo realizável na infra do cliente sem Azure obrigatório**; segredos e identidade sob controle do cliente.
- Negativas / dívidas assumidas: maior complexidade de identidade (gMSA + app-only CBA) e de testes de autorização; RLS exige disciplina de esquema; **HA exige mecanismo de segredos multi-nó** — DPAPI de nó único não basta ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)); rotação de certificados é encargo operacional.
- Riscos e mitigação: regressão de isolamento → testes de autorização e de arquitetura no CI (§37); exposição de dados pessoais → avaliação de dados/DPO e minimização; comprometimento de segredo → custódia on-premises + redaction + **reimage do worker após manipulação de SAS** (§34).

## Evidências

Runbook [§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco), [§30](../runbook/05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos), [§31](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções), [§32](../runbook/05-parte-v-seguranca-infra-operacao.md#32-segredos-e-material-criptográfico), [§33](../runbook/05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida), [§34](../runbook/05-parte-v-seguranca-infra-operacao.md#34-hardening-dos-workers-windows), [§35](../runbook/05-parte-v-seguranca-infra-operacao.md#35-malware-e-conteúdo-hostil), [§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência), [§7.2](../runbook/02-parte-ii-arquitetura.md#72-topologia-de-rede).

O gate exige **threat model on-premises + avaliação de dados/privacidade**. Esses artefatos estão em [`evidence/0008-threat-model-avaliacao-dados.md`](evidence/0008-threat-model-avaliacao-dados.md) (Evidence Owner: Engenharia), **pendentes de revisão de Segurança/DPO**. Este ADR permanece **`proposto`** até a revisão registrada e a **aceitação formal do Decision Owner** (Vinicius Miranda).
