# ADR-0001 — Monólito modular no plano de controle e workers isolados

- **Status:** aceito _(efetivado no merge deste PR pelo Decision Owner)_
- **Data:** 2026-07-20 (proposto) · 2026-07-21 (aceito)
- **Gate de aprovação:** Decision Owner + revisão Dev/Tech Lead
- **Substitui / substituído por:** —

## Registro de aceitação

- **Decision Owner:** Vinicius Miranda — aceitação formal efetivada com o
  merge do PR que anexa a evidência (data: 2026-07-21).
- **Revisão necessária (Dev/Tech Lead):** parecer arquitetural em
  [`evidence/0001-parecer-arquitetural.md`](evidence/0001-parecer-arquitetural.md),
  produzido pela engenharia; recomendação técnica de aceitar com ressalvas.
  Não havendo Tech Lead distinto, a competência é exercida/aceita pelo
  Decision Owner (exceção de bootstrap registrada na
  [matriz](gate-closure-matrix.md)).
- **Evidence Owner:** Engenharia ArchiveBridge.
- **Ressalvas aceitas:** (1) o scaffolding deve incluir o projeto de testes
  de arquitetura com as regras de dependência da §8.1 desde o início;
  (2) "workers isolados" pressupõe a topologia de rede da §7.2, sustentada
  pelo ADR-0008; (3) o isolamento de blast radius é propriedade
  pretendida — a ser comprovada, após o scaffolding, por testes de falha,
  recuperação e blast radius.

## Contexto

O runbook define, em [§6 Princípios arquiteturais](../runbook/02-parte-ii-arquitetura.md#6-princípios-arquiteturais) e [§7 Componentes e responsabilidades](../runbook/02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades), que o plano de controle é um deployable coeso com limites internos explícitos, enquanto o processamento pesado (PST, upload, connector de origem, reconciliação, assinatura de evidência) roda em processos separados. A [§8 Estrutura do repositório](../runbook/02-parte-ii-arquitetura.md#8-estrutura-do-repositório) organiza o código como monólito modular com pastas de adapters isolando dependências de fornecedor.

## Decisão

O plano de controle (`Control.Api`, `Control.Orchestrator` e os módulos de domínio) é **um único deployable monolítico modular** com fronteiras internas explícitas entre módulos. O processamento pesado é executado por **workers isolados** em unidades de implantação separadas (`pst-worker-windows`, `upload-worker-windows`, `source-connector-windows`, `recon-worker`, `evidence-signer`), cada um com identidade e fronteira de rede próprias. A arquitetura é **hexagonal**: o domínio não conhece SDK de Azure, Veritas/Enterprise Vault, Purview ou Graph; interfaces vivem em `Application`, adapters em `Infrastructure`. Regras de dependência (§8.1) são verificadas por testes de arquitetura que falham o build.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Microsserviços desde o início | escala/deploy independentes | custo transacional, complexidade operacional prematura | overhead sem benefício nesta fase (§6, princípio 11) |
| Processo único incluindo os workers | simplicidade de deploy | PST malformado derruba o plano de controle; sem isolamento de blast radius | viola isolamento e fail-closed (§6, §30 DoS/EoP) |
| Monólito em camadas sem fronteiras de módulo | familiar | acoplamento entre módulos, erosão de limites | testes de arquitetura não teriam contrato a impor |

## Consequências

- Positivas: menor custo transacional; limites de módulo aplicáveis por `NetArchTest`; workers escalam e são endurecidos de forma independente.
- Negativas / dívidas assumidas: disciplina de fronteira depende de testes de arquitetura sempre verdes; composição por módulo exige governança de dependências.
- Riscos e mitigação: erosão de limites → testes de arquitetura obrigatórios no CI; crescimento do monólito → revisão periódica das fronteiras de módulo.

## Evidências

Runbook [§6](../runbook/02-parte-ii-arquitetura.md#6-princípios-arquiteturais), [§7](../runbook/02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades), [§8](../runbook/02-parte-ii-arquitetura.md#8-estrutura-do-repositório) e diagramas de componentes/topologia. Confirmação do gate: parecer do arquiteto e do tech lead no PR de aprovação.
