# Parecer arquitetural — evidência do gate do ADR-0001

Evidência requerida pelo gate do
[ADR-0001](../0001-monolito-modular-e-workers-isolados.md) (monólito
modular no plano de controle + workers isolados; arquitetura hexagonal).

- **Tipo:** parecer técnico (revisão Dev/Tech Lead)
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge, sob direção
  do Decision Owner
- **Data:** 2026-07-21
- **Natureza:** análise técnica de engenharia. Não constitui, por si, a
  aceitação formal — esta é ato do Decision Owner (ver "Recomendação").

## 1. Objeto da revisão

A decisão avaliada tem três componentes (runbook
[§6](../../runbook/02-parte-ii-arquitetura.md#6-princípios-arquiteturais),
[§7](../../runbook/02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades),
[§8](../../runbook/02-parte-ii-arquitetura.md#8-estrutura-do-repositório)):

1. plano de controle como **monólito modular** único (Control.Api,
   Orchestrator e módulos de domínio) com fronteiras internas explícitas;
2. processamento pesado em **workers isolados** por unidade de implantação,
   cada um com identidade e fronteira de rede próprias;
3. **arquitetura hexagonal** — domínio sem SDK de fornecedor; interfaces em
   `Application`, adapters em `Infrastructure`; regras de dependência
   verificadas por testes de arquitetura.

## 2. Análise técnica

### 2.1 Aderência aos requisitos

| Requisito do runbook | Como a decisão atende |
| --- | --- |
| Isolamento de blast radius (§6 fail closed; §30 DoS/EoP) | PST malformado derruba, no máximo, um worker isolado — não o plano de controle |
| Segregação de identidade e menor privilégio (§31) | cada worker é unidade de implantação com Managed Identity própria; alinha-se ao ADR-0008 |
| Custo transacional baixo, sem microsserviços prematuros (§6, princípio 11) | monólito modular evita fan-out de rede/deploy sem perder fronteiras |
| Domínio agnóstico de fornecedor (§6 hexagonal; §8.1) | interfaces no núcleo; adapters isolam Aspose/EV/Purview/Graph; trocar fornecedor (ex.: ADR-0013) não toca o domínio |
| Fronteiras aplicáveis (§8.1) | `NetArchTest`/equivalente falha o build ao violar regra de dependência — a fronteira é executável, não convencional |

### 2.2 Riscos e mitigação

| Risco | Severidade | Mitigação já prevista |
| --- | --- | --- |
| Erosão das fronteiras de módulo com o tempo | média | testes de arquitetura obrigatórios no CI; revisão periódica das fronteiras |
| Monólito crescer a ponto de dificultar deploy | baixa nesta fase | módulos com limites explícitos permitem extrair um serviço se e quando justificado por dados |
| Acoplamento acidental via composição (composition root) | baixa | Api/Workers como únicos composition roots; módulo A não referencia infraestrutura de B (§8.1) |

### 2.3 Alternativas

As alternativas do ADR (microsserviços desde o início; processo único
incluindo workers; monólito em camadas sem fronteiras) foram consideradas
e a rejeição é tecnicamente sólida: a primeira impõe custo operacional sem
benefício nesta fase; a segunda elimina o isolamento de blast radius que é
requisito de segurança; a terceira remove o contrato que os testes de
arquitetura impõem.

### 2.4 Consistência com o restante do conjunto

A decisão é pré-condição coerente de ADR-0003 (outbox/inbox no plano de
controle), ADR-0008 (isolamento por workload) e ADR-0013 (adapters de
exportação isolam o EV). Não há conflito detectado com os demais ADRs.

## 3. Ressalvas

- A eficácia depende de os testes de arquitetura existirem e permanecerem
  verdes no CI **desde o primeiro código** — recomenda-se que o
  scaffolding (seção 10) já inclua o projeto de testes de arquitetura com
  pelo menos as regras de dependência da §8.1.
- "Workers isolados" pressupõe a topologia de rede da §7.2 (sem IP
  público, private endpoints, NSG negando lateral); o parecer assume que
  ADR-0008 sustentará esses controles.

## 4. Recomendação

Do ponto de vista de engenharia, a decisão é **sólida, aderente ao runbook
e sem bloqueios técnicos** — recomenda-se a aceitação, observadas as
ressalvas da seção 3.

A **aceitação formal** é ato do Decision Owner (Vinicius Miranda) e
**efetiva-se com o merge do PR** que anexa este parecer e altera o status
do ADR-0001 para `aceito`. Enquanto não houver merge, o ADR permanece
`proposto`.
