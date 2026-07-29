# ADR-0008 — Modelo de isolamento por tenant/projeto (identidade, segredos e rede on-premises)

- **Status:** **aceito arquiteturalmente com condições** pelo Decision Owner em 2026-07-28 (assinatura institucional de Segurança/DPO pendente antes de produção; **não bloqueia o início do desenvolvimento**)
- **Data:** 2026-07-20 (versão original) · 2026-07-23 (reescrito — on-premises) · 2026-07-28 (aceito arquiteturalmente pelo Decision Owner)
- **Decision Owner:** Vinicius Miranda
- **Revisor necessário:** Segurança/Privacidade (DPO)
- **Gate de aprovação:** aceitação arquitetural do Decision Owner sobre o parecer técnico (**APROVAÇÃO TÉCNICA CONDICIONAL**); assinatura institucional de Segurança/DPO obrigatória **antes de produção**
- **Substitui / substituído por:** reescreve a versão anterior deste ADR (isolamento expresso em primitivos Azure — Managed Identity, private endpoints, NSG), que **não** foi aceita. Nome do arquivo mantido por estabilidade de referências.

## Registro de aceitação

- **Decision Owner:** Vinicius Miranda — **decisão de aceitação em 2026-07-28**.
- **Decisão:** **aceito arquiteturalmente com condições.**
- **Fundamento:** parecer técnico de Segurança e Privacidade concluído (resultado **APROVAÇÃO TÉCNICA CONDICIONAL**, [`evidence/0008-parecer-tecnico-seguranca-privacidade.md`](evidence/0008-parecer-tecnico-seguranca-privacidade.md)); desenho on-premises tecnicamente adequado; condições **SEC-01..04, IAM-01..03, NET-01, PRIV-01..04, IR-01** já incorporadas (seção 7 da [evidência](evidence/0008-threat-model-avaliacao-dados.md)).

> O ADR-0008 está **aceito arquiteturalmente pelo Decision Owner, condicionado aos controles documentados no parecer técnico**. A **assinatura institucional de Segurança/DPO permanece pendente como requisito anterior à produção, mas não bloqueia o início do desenvolvimento**. **A aceitação deste ADR não equivale à autorização de produção.**

- **Perfil inicial autorizado:** nó único; **DPAPI sob identidade dedicada**; **workers separados**; **identidade por workload**; **RLS como defesa em profundidade**; **autorização também na Application**.
- **Perfil HA de segredos:** **`BLOCKED_PENDING_EVIDENCE`** — o HA **não** pode ser habilitado até existir **solução concreta e certificada** para segredos multi-nó.
- **Pendências mantidas:**
  - **assinatura institucional de Segurança/DPO** — pendente **antes de produção**;
  - **controles de implementação** (SEC-01, IAM-01..03, …) — obrigatórios **no primeiro scaffolding**;
  - **controles operacionais** — obrigatórios **antes de produção**;
  - **HA de segredos** — **bloqueado**.
- **Assinaturas institucionais e parecer jurídico:** **não preenchidos** — nenhuma assinatura de Segurança/DPO e nenhum parecer jurídico foram declarados ou falsificados.

## Contexto

A [§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco) exige `tenant_id` em todas as tabelas e índices que começam por tenant nos acessos multitenant. O [§30 Threat model](../runbook/05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos) lista "Cross-tenant access" com controle obrigatório `tenant key, RLS, authorization tests`. A [§31 Identidade e segregação de funções](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções) define personas/workloads com identidade separada e **sem secret compartilhado**. A [§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência) proíbe um módulo referenciar a infraestrutura de outro.

A baseline arquitetural vigente é **on-premises** ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)): identidade, segredos, rede, storage e workers ficam na infraestrutura do cliente; o **Microsoft 365 é destino externo** (saída HTTPS 443). A versão anterior deste ADR expressava o isolamento em primitivos Azure (Managed Identity, Key Vault, private endpoints, NSG). O ADR-0003 registrou, entre suas **pendências downstream**, exatamente a "revisão do ADR-0008 (identidade/rede on-prem)"; este ADR a executa.

## Precedência sobre o runbook

O runbook v1.0 expressa o modelo em PaaS Azure — Managed Identity ([§31](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções)), Key Vault ([§32](../runbook/05-parte-v-seguranca-infra-operacao.md#32-segredos-e-material-criptográfico)), storage account/private endpoint ([§33](../runbook/05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida)), IaC Azure/NSG/private endpoints (§36) e topologia de rede ([§7.2](../runbook/02-parte-ii-arquitetura.md#72-topologia-de-rede)). Conforme o [ADR-0003](0003-azure-sql-e-service-bus-premium.md), esses pressupostos PaaS **não** são a baseline vigente. Este ADR reconcilia o modelo de isolamento com a baseline on-premises, usando o mesmo padrão de emenda já aplicado em ADR-0003/§9, ADR-0007/§9 e ADR-0006. O DOCX fonte **não** é alterado agora; a revisão da Parte V para a v1.1 é ação pendente do owner do documento. **Os objetivos de controle das §30–§35 são preservados integralmente** — muda a *realização técnica*, não o objetivo.

## Decisão

Isolamento **por tenant e por projeto**, realizado com primitivos **on-premises**, em camadas:

### Dados

- `tenant_id` em todas as tabelas; índices liderados por tenant; **tenant e projeto explícitos nos contratos**;
- **Row-Level Security do SQL Server** — recurso **nativo do SQL Server, válido on-premises** — mais **testes de autorização** que falham o build ao vazar dados entre tenants. O controle obrigatório da §30 (`tenant key, RLS, authorization tests`) permanece **inalterado**, pois o RLS é do próprio SQL Server (sistema de registro do ADR-0003);
- **RLS é defesa em profundidade, não a autorização única.** O RLS vive na Infrastructure; as **regras de autorização permanecem na Application**; **nenhuma consulta depende exclusivamente do *session context* do SQL**; os testes cross-tenant validam **Application + SQL**.

> **Testes de isolamento e o scaffolding.** O runbook mantém o scaffolding .NET **bloqueado** até a aceitação dos ADRs; portanto **não há código a testar antes do scaffolding**. Os **testes de arquitetura, autorização e isolamento cross-tenant** devem **existir no primeiro PR de scaffolding** e permanecer **obrigatórios desde o primeiro módulo** que persista ou consulte dados escopados por tenant.

Este ADR define **objetivos de identidade e isolamento**; **cada adapter define os papéis concretos** necessários. Princípios:

- **uma identidade de serviço Windows por workload local** (Control / EV / PST / Upload / Recon / Evidence): **gMSA ou virtual service account**, **nunca Domain Admin** (§34); **sem secret compartilhado** entre workloads; RBAC mínimo por função e **segregação de funções** da §31 (quem prepara não aprova; quem aprova não altera artefato);
- **Source Connector:** certificado por instalação (enrollment mTLS) — já on-premises na §31;
- **cada operação externa possui identidade própria e permission set mínimo.** Purview (operador humano), precheck programático, reconciliação e Graph condicional **não compartilham identidade por padrão** — **não** há uma única identidade genérica "M365". Quando programático, o acesso ao destino Microsoft 365 usa **app-only por certificado (CBA)** no Entra ID, com **role mínimo** (**Exchange Online RBAC for Applications** — [ADR-0007](0007-graph-fts-bloqueado.md)) e management scope restrito às mailboxes da onda. **Managed Identity aplica-se apenas quando um componente estiver de fato hospedado em Azure** — não é a baseline on-premises (§31 já diz "Managed Identity é preferida **em Azure**").

Objetivos de identidade por função (os papéis concretos e escopos ficam nos adapters):

| Identidade | Função |
| --- | --- |
| `ArchiveBridge-Control` | API e orquestração local |
| `ArchiveBridge-EvWorker` | acesso ao Enterprise Vault |
| `ArchiveBridge-PstWorker` | processamento local de PST |
| `ArchiveBridge-UploadWorker` | upload AzCopy (custódia do SAS) |
| `ArchiveBridge-ReconWorker` | consultas de reconciliação |
| Purview Operator (humano) | criação/início do job no portal |
| Purview Approver (humano) | aprovação quatro-olhos |
| M365 Precheck App | leituras mínimas de mailbox/archive |
| Graph FTS App | adapter condicional, atualmente **bloqueado** (ADR-0007) |
| Evidence Signer | assinatura de evidências |

### Segredos e material criptográfico

- custódia pelo **mecanismo de segredos on-premises** ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)), com baseline explícito para o release inicial:
  - **perfil de nó único: DPAPI**;
  - **perfil HA de segredos: `BLOCKED_PENDING_EVIDENCE`** — só habilitado depois de **escolher e certificar** um mecanismo multi-nó concreto (ex.: key ring compartilhado protegido por certificado; store corporativo homologado; HSM; solução de secrets management já existente no cliente). Uma interface vaga "multi-nó" **não** conta como solução pronta;
  - **Certificate Store** do Windows para certificados; **ACLs exclusivas**;
- **SAS do Purview** custodiado por esse mecanismo ([ADR-0006](0006-purview-adapter-ga-inicial.md)), com validação de host/HTTPS/container/expiry, tags de wave, leitura restrita à identidade do upload worker e **destruição das cópias locais após o upload** (o produto **não** promete revogação remota do SAS — ADR-0006);
- **chaves HMAC por tenant, versionadas** (`keyVersion` persistida; rotação **não** invalida fingerprints antigos) — controle de nível de aplicação, inalterado;
- **assinatura do evidence package com chave não exportável** (TPM/HSM local quando a exigência justificar);
- **renovação de certificados** automatizada, com alarmes 30/14/7 dias;
- **redaction central** de toda telemetria ([§32.1](../runbook/05-parte-v-seguranca-infra-operacao.md#321-redaction-central)), com testes de *canary* que falham o build — inalterado.

### Rede

- **Nenhuma entrada proveniente da internet.**
- **Egress externo somente HTTPS 443** aos endpoints Microsoft **autorizados** (Entra ID, Exchange Online, Graph, Purview e o Azure Storage temporário do Purview) — [ADR-0003](0003-azure-sql-e-service-bus-premium.md).
- **Fluxos internos** (Control Plane ↔ SQL Server; workers ↔ SQL Server; workers ↔ NAS/SMB; worker ↔ Enterprise Vault; mTLS entre componentes; navegadores internos ↔ Portal) utilizam **portas explicitamente registradas na matriz de fluxos e portas** (ADR-0003), com **segmentação por firewall/VLAN do cliente** negando tráfego lateral desnecessário. Ou seja: o "somente 443" é a regra de **egress externo**, não dos fluxos internos aprovados.
- Controles Azure de rede (NSG, private endpoints, ausência de IP público) **não se aplicam** à baseline on-premises (ver "Evolução futura — perfis em Azure").

### Dados mínimos e fail-closed

- o plano de controle guarda **apenas metadados**; o conteúdo permanece nos artefatos protegidos em storage local/NAS/SMB (ADR-0003), com ciclo de vida por container ([§33](../runbook/05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida)) e hardening dos workers ([§34](../runbook/05-parte-v-seguranca-infra-operacao.md#34-hardening-dos-workers-windows)); malware/conteúdo hostil tratado por [§35](../runbook/05-parte-v-seguranca-infra-operacao.md#35-malware-e-conteúdo-hostil);
- **fail closed** na ausência de identidade, consentimento ou autorização.

## Evolução futura — perfis em Azure (fora do release inicial)

Perfis de implantação baseados em Azure **não fazem parte do release inicial**. Caso sejam requeridos futuramente, serão definidos por **ADR específico**, **sem alterar os contratos do domínio** (identidade, segredos e rede permanecem atrás das mesmas abstrações — ADR-0003, "adapters opcionais futuros"). Este ADR **não** projeta agora Managed Identity, Key Vault, NSG ou private endpoints — evitando complexidade acidental e escopo que não será implementado.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Deployment single-tenant por cliente | isolamento físico forte | custo e sobrecarga operacional altos por cliente | inviável como padrão multitenant |
| Filtragem só na aplicação (sem RLS) | simples | uma falha de query vaza dados entre tenants | §30 exige RLS + authorization tests |
| Identidades/segredos compartilhados entre workloads | menos configuração | quebra segregação de funções e menor privilégio | proibido pela §31 |
| Exigir Managed Identity/Key Vault Azure como mecanismo **obrigatório** | primitivos gerenciados | exige Azure; contraria a baseline on-premises (ADR-0003) | on-premises: gMSA/CBA + DPAPI (HA de segredos `BLOCKED_PENDING_EVIDENCE`); Azure só como perfil futuro por ADR específico |

## Consequências

- Positivas: garantia forte contra acesso cross-tenant; menor privilégio por workload; blast radius reduzido; **modelo realizável na infra do cliente sem Azure obrigatório**; segredos e identidade sob controle do cliente.
- Negativas / dívidas assumidas: maior complexidade de identidade (gMSA + app-only CBA por operação) e de testes de autorização; RLS exige disciplina de esquema; **o perfil HA de segredos fica `BLOCKED_PENDING_EVIDENCE`** até um mecanismo multi-nó concreto ser escolhido e certificado ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)); rotação de certificados é encargo operacional.
- Riscos e mitigação: regressão de isolamento → testes de autorização e de arquitetura no primeiro PR de scaffolding (§37); exposição de dados pessoais → avaliação de dados/DPO e minimização; comprometimento de segredo → custódia on-premises + redaction. **Higiene padrão do upload worker após cada uso do SAS** (destruir cópia local, encerrar o processo, limpar temporários, verificar logs e dumps, health check); **reimage apenas após incidente, comprometimento ou suspeita de exposição** (§34) — **não** como rotina. Um perfil realmente descartável pode existir no futuro, com desenho operacional próprio.

## Evidências

Runbook [§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco), [§30](../runbook/05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos), [§31](../runbook/05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções), [§32](../runbook/05-parte-v-seguranca-infra-operacao.md#32-segredos-e-material-criptográfico), [§33](../runbook/05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida), [§34](../runbook/05-parte-v-seguranca-infra-operacao.md#34-hardening-dos-workers-windows), [§35](../runbook/05-parte-v-seguranca-infra-operacao.md#35-malware-e-conteúdo-hostil), [§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência), [§7.2](../runbook/02-parte-ii-arquitetura.md#72-topologia-de-rede).

O gate exige **threat model on-premises + avaliação de dados/privacidade**. Esses artefatos estão em [`evidence/0008-threat-model-avaliacao-dados.md`](evidence/0008-threat-model-avaliacao-dados.md) (Evidence Owner: Engenharia).

Uma **revisão técnica preliminar de Segurança e Privacidade** foi produzida em [`evidence/0008-parecer-tecnico-seguranca-privacidade.md`](evidence/0008-parecer-tecnico-seguranca-privacidade.md), com resultado **APROVAÇÃO TÉCNICA CONDICIONAL**: o desenho on-premises é tecnicamente adequado, **sujeito às condições** SEC-01..04, IAM-01..03, NET-01, PRIV-01..04 e IR-01 (incorporadas à seção 7 da evidência) — que valem como correções e como gates verificáveis de scaffolding/produção. O perfil **HA de segredos permanece `BLOCKED_PENDING_EVIDENCE`**. Essa revisão **não** é assinatura institucional de Segurança/DPO (pendente) nem parecer jurídico, e **não** autoriza produção.

Este ADR está **aceito arquiteturalmente com condições** pelo Decision Owner (Vinicius Miranda) em **2026-07-28** — ver "Registro de aceitação". A **assinatura institucional de Segurança/DPO permanece pendente como requisito anterior à produção** (não bloqueia o início do desenvolvimento), e a **aceitação do ADR não equivale a autorização de produção**.
