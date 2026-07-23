# ADR-0006 — Purview Network Upload como adapter GA inicial

- **Status:** proposto
- **Data:** 2026-07-20
- **Decision Owner:** Vinicius Miranda (aceitação formal pendente)
- **Revisor necessário:** responsável técnico pelo tenant Microsoft 365
- **Gate de aprovação:** relatório de validação do Purview em tenant controlado + referências oficiais Microsoft
- **Substitui / substituído por:** —

## Contexto

A [§24 Strategy e capability gates](../runbook/04-parte-iv-destinos-m365.md#24-strategy-e-capability-gates) estabelece que "Microsoft 365" não é uma única capacidade: cada destino é avaliado por `ITargetIngestor` antes de aceitar uma onda. A [§25 Adapter Purview Network Upload - caminho GA](../runbook/04-parte-iv-destinos-m365.md#25-adapter-purview-network-upload---caminho-ga) define o Purview Network Upload (AzCopy + CSV mapping oficial) como o adapter habilitado no primeiro release, que **prepara e transporta**, enquanto a criação/início do job permanece no portal Purview. O bloqueio de >100 GB no mesmo archive (`MICROSOFT_ASSESSMENT_REQUIRED`) é mantido.

A baseline arquitetural vigente é **on-premises** ([ADR-0003](0003-azure-sql-e-service-bus-premium.md)): o produto é instalado na infraestrutura do cliente, sem Control Plane SaaS nem Azure PaaS obrigatório, com SQL Server local como sistema de registro, e o **Microsoft 365 é o destino externo** da migração. Este ADR trata o Purview Network Upload nesse enquadramento — um **adapter de destino externo**, não uma dependência de hospedagem (ver "Alinhamento com a baseline on-premises").

> **Catálogo:** registrado no
> [catálogo de adapters de destino](target-adapter-catalog.md) como
> `PurviewPstImportAdapter` — papel `PRIMARY_GA_TARGET`, implementação
> `NOT_IMPLEMENTED`, gate `PENDING_ADR_0006`, **estado-alvo** `ENABLED`. É o
> adapter GA **inicial planejado** para PST (primeiro destino, não o único),
> **ainda não habilitado em produção** — o Graph permanece condicional
> (ADR-0007), preservando destinos evoluíveis.

## Decisão

**Purview Network Upload** é o **único adapter de destino habilitado no primeiro release**, atrás de `ITargetIngestor`. O produto — instalado on-premises — **prepara parts localmente** (SQL Server local + staging local/NAS/SMB, [ADR-0003](0003-azure-sql-e-service-bus-premium.md)), gera o CSV mapping oficial e **transporta via AzCopy homologado, a partir de um upload worker on-premises**, para o staging temporário provido pela Microsoft; a **criação e o início do import job permanecem como tarefa de workflow humana no portal Purview** (§25.9), conforme orientação da Microsoft. O capacity gate (§25.4) é obrigatório e **bloqueia >100 GB para o mesmo archive** com estado `MICROSOFT_ASSESSMENT_REQUIRED`; auto-expanding archive não é bypass do limite do adapter.

## Alinhamento com a baseline on-premises (ADR-0003)

O runbook v1.0 foi redigido antes da fixação da baseline on-premises; esta seção reconcilia o adapter Purview com a decisão vigente do [ADR-0003](0003-azure-sql-e-service-bus-premium.md), usando o mesmo padrão de emenda de governança já aplicado em ADR-0003/§9 e ADR-0007/§9.

1. **Purview/M365 = destino externo, não dependência de hospedagem.** Nenhum componente de runtime do produto depende de Azure PaaS e **não há assinatura Azure do cliente**. A conectividade exigida é **somente outbound HTTPS 443** aos endpoints Microsoft necessários (Entra ID, Exchange Online, Purview e o storage temporário do próprio Purview via SAS); **sem portas de entrada**. Isso é consistente com "Microsoft 365 apenas como destino externo" do ADR-0003.
2. **O container `ingestiondata` é staging temporário provido pela Microsoft**, alcançado pela URL SAS que o operador obtém no portal Purview (§25.5) — **não é storage de uma assinatura Azure do cliente**. Sua retenção é controlada pela Microsoft (§25.10); o produto não promete deleção imediata desse staging.
3. **O AzCopy executa a partir do upload worker on-premises.** O "worker efêmero dedicado" do runbook v1.0 (§25.5–§25.6) materializa-se, na baseline on-premises, como **host/serviço dedicado e endurecido no ambiente do cliente** (Windows Service, sem usuários interativos, admin JIT, sessão de vida curta) — não uma VM efêmera em nuvem. Como o SAS inevitavelmente aparece na command line do processo AzCopy (§25.6), o isolamento desse worker é requisito, não recomendação.
4. **Reconciliação do segredo SAS.** O runbook v1.0 §25.5 descreve armazenar o SAS "no Key Vault" e lê-lo pela "managed identity do upload worker" — primitivos de Azure. Na baseline vigente (ADR-0003, sem Azure PaaS obrigatório), o SAS é custodiado pelo **mecanismo de segredos on-premises** (DPAPI em nó único; mecanismo de segredo multi-nó em configuração HA — ADR-0003), preservando **todas** as propriedades funcionais exigidas pelo runbook: campo não ecoado; nunca em log, analytics ou telemetria; validação de host/HTTPS/container/expiry/permissões; expiração e tags de wave; leitura restrita à identidade do upload worker; eliminação/desabilitação após o upload e a janela de investigação. O detalhamento do modelo de identidade e segredos on-premises é objeto do **[ADR-0008](0008-isolamento-por-tenant-e-projeto.md)**; este ADR apenas registra a reconciliação da divergência do runbook v1.0.
5. **Submissão sem transação distribuída — ledger `external_operations` (ADR-0003).** O upload via AzCopy e a criação/início do import job no portal Purview produzem **efeito externo** fora do alcance de qualquer transação local. Conforme o contrato de execução durável do [ADR-0003](0003-azure-sql-e-service-bus-premium.md), essas etapas são registradas no ledger `external_operations` (`INTENT → SUBMITTED → CONFIRMED | AMBIGUOUS | FAILED`), com **chave visível ao provedor** (nome/ID do Purview job, §25.9) para reconciliação idempotente; a reconciliação pós-import (§26) confirma o efeito. Não se presume atomicidade entre o produto on-premises e o serviço Microsoft.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Graph Mailbox Import/Export (FTS) | API programática | não recebe PST; não-GA para o cenário | bloqueado (ADR-0007) |
| Adapter contratual/partner de ingestão rápida | possível throughput | exige contrato/SDK e projeto separado (§29) | fora do primeiro release |
| Criar/iniciar o job via API em vez do portal | menos passos manuais | não suportado pela orientação atual da Microsoft | fail-closed: só caminho suportado |
| Hospedar componentes do produto em Azure (Key Vault/managed identity) para o handling do SAS | reusaria primitivos gerenciados | contradiz a baseline on-premises (ADR-0003): exigiria assinatura Azure do cliente e componente fora do ambiente do cliente | rejeitado: SAS custodiado pelo mecanismo de segredos on-premises (ADR-0008) |

## Consequências

- Positivas: caminho GA/suportado, com evidência oficial; menor risco regulatório e de suporte; **destino externo compatível com a baseline on-premises** — sem assinatura Azure do cliente e sem Control Plane SaaS.
- Negativas / dívidas assumidas: etapas humanas no portal (workflow, não cliques fora do sistema); dependência do staging Microsoft (§25.10); **o SAS transita na command line do AzCopy** (§25.6), o que exige worker on-premises dedicado e endurecido (item 3).
- Riscos e mitigação: mudança na documentação Microsoft → bloquear feature e registrar ADR (§2/§3); cenário >100 GB → pacote para suporte Microsoft e estado `WAITING_EXTERNAL` (§27); **exposição do SAS** → custódia pelo mecanismo de segredos on-premises e redação em logs/telemetria (ADR-0008).

## Evidências

Runbook [§24](../runbook/04-parte-iv-destinos-m365.md#24-strategy-e-capability-gates), [§25](../runbook/04-parte-iv-destinos-m365.md#25-adapter-purview-network-upload---caminho-ga), [§27](../runbook/04-parte-iv-destinos-m365.md#27-cenários-acima-de-100-gb-no-mesmo-archive). Referências oficiais: PST Import overview / Network upload / Troubleshooting / FAQ — Apêndice F.

O gate exige **relatório de validação do Purview em tenant controlado**. O **protocolo de validação** (escopo, pré-requisitos, casos e critérios de aceitação) está em [`evidence/0006-plano-validacao-purview.md`](evidence/0006-plano-validacao-purview.md); a **execução em tenant é externa e permanece pendente** — o Evidence Owner e o responsável técnico pelo tenant ainda serão atribuídos. Este ADR permanece **`proposto`** até o relatório de validação estar anexado e a **aceitação formal do Decision Owner** (Vinicius Miranda) ser registrada.
