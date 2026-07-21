# Matriz de fechamento dos gates de aprovação

Princípio de governança: **o merge dos documentos ADR não equivale à
aprovação das decisões.** Não é necessário um aprovador diferente por ADR;
o objetivo é identificar **quais revisões e evidências são realmente
necessárias** antes de o Decision Owner aceitar formalmente cada decisão.

## Quatro papéis distintos

| Papel | O que é | Quem |
| --- | --- | --- |
| **Decision Owner** | decisão final do produto sobre a arquitetura; aceita ou não o ADR | **Vinicius Miranda** (todos os ADRs) |
| **Revisores necessários** | revisão especializada, **apenas quando o tema exige** competência específica (técnica ou jurídica) | por ADR (coluna abaixo) |
| **Evidence Owner** | produz o teste, relatório, parecer ou validação exigido pelo gate; pode ser outra pessoa | atribuído ao iniciar a evidência |
| **Aprovação formal** | ocorre **somente depois** de as evidências mínimas e revisões estarem registradas; muda o `Status` do ADR | ato do Decision Owner |

Estes quatro papéis são deliberadamente separados: **decidir** (Decision
Owner), **revisar** (especialista, quando necessário), **produzir a
evidência** (Evidence Owner) e **aprovar formalmente** (Decision Owner,
após evidências) são atos distintos e não se presumem uns aos outros.

## Registro obrigatório de cada aprovação

Ao fechar um gate, o ADR correspondente deve registrar:

1. **Decision Owner** e **data** da aceitação formal;
2. **Revisores** que participaram (quando exigidos) e seus pareceres;
3. **Evidência** anexada (link para relatório, teste, parecer, contrato ou PR)
   e o **Evidence Owner** que a produziu;
4. **Condições e ressalvas**, se houver.

Só então o `Status` do ADR muda de `proposto` para `aceito` e a tabela de
`README.md` é atualizada — no mesmo PR que anexa a evidência. **Nenhum ADR
tem o status alterado nesta matriz.**

## Matriz

| ADR | Decision Owner | Revisores necessários | Evidência requerida | Evidence Owner | Status |
| --- | --- | --- | --- | --- | --- |
| [0001](0001-monolito-modular-e-workers-isolados.md) | Vinicius Miranda | Dev/Tech Lead | parecer arquitetural — [evidência](evidence/0001-parecer-arquitetural.md) | Engenharia | **aceito** (merge de 2026-07-21) |
| [0002](0002-dotnet-10-lts-e-politica-de-atualizacao.md) | Vinicius Miranda | Dev + Segurança | política de runtime, atualização e patching | a atribuir | proposto |
| [0003](0003-azure-sql-e-service-bus-premium.md) | Vinicius Miranda | Dev/Cloud ou FinOps | estimativa inicial de custos Azure | a atribuir | proposto |
| [0004](0004-aspose-email-engine-primaria.md) | — | — | — | — | **substituído** pelo [ADR-0013](0013-exportacao-ev-multiversao.md) em 2026-07-20, antes de aprovação |
| [0005](0005-libpff-validador-independente.md) | Vinicius Miranda | Jurídico | análise de compatibilidade e licença LGPL | a atribuir | proposto |
| [0006](0006-purview-adapter-ga-inicial.md) | Vinicius Miranda | responsável técnico pelo tenant | relatório de validação do Purview | a atribuir | proposto |
| [0007](0007-graph-fts-bloqueado.md) | Vinicius Miranda | Segurança/Arquitetura | evidência da rota PST/EV → FTS → Graph — `GraphFtsImportFromPstEv = BLOCKED_PENDING_EVIDENCE` (Graph permanece adapter condicional; sem bloqueio global) — [evidência](evidence/0007-evidencia-microsoft-bloqueio.md) | Engenharia | **aceito** pelo Decision Owner em 2026-07-21 (vigência no merge do PR #9) |
| [0008](0008-isolamento-por-tenant-e-projeto.md) | Vinicius Miranda | Segurança/Privacidade | threat model e avaliação de dados | a atribuir | proposto |
| [0013](0013-exportacao-ev-multiversao.md) | Vinicius Miranda | Dev + Segurança | revisão técnica do capability discovery e dos adapters | a atribuir | proposto |

Legenda de estado:

- **proposto** — decisão registrada, aguardando as revisões necessárias e a
  evidência requerida antes da aceitação formal do Decision Owner;
- **aceito** — Decision Owner aceitou formalmente após revisões e evidência
  registradas (nenhum ADR está neste estado ainda);
- **substituído** — ADR retirado antes de aprovação por decisão registrada;
  o ADR substituto entra na matriz com seus próprios revisores/evidência.

Sobre os **revisores necessários**: são exigidos apenas quando o tema pede
competência específica; não implicam uma pessoa distinta por ADR. O mesmo
revisor pode cobrir vários ADRs, e o Decision Owner pode exercer um papel de
revisão quando detiver a competência — o que a matriz garante é que a
competência necessária foi efetivamente exercida e registrada, não que os
papéis sejam pessoas diferentes.

### ADR-0007: adapter condicional vs. rota bloqueada

O que se aprova no ADR-0007 é manter o Graph como **adapter condicional**
(`GraphFtsTargetAdapter`, `CONDITIONAL`), com a **rota PST/EV → FTS**
específica em `GraphFtsImportFromPstEv = BLOCKED_PENDING_EVIDENCE` — não um
bloqueio global do Graph. Decisão aprovável hoje por Arquitetura +
Segurança. A promoção segue o ciclo `BLOCKED_PENDING_EVIDENCE → CANDIDATE →
CERTIFIED → ENABLED` (config versionada + capability evidence); **novo ADR**
só se mudar contrato, segurança ou arquitetura. Assim, o fechamento dos
gates vigentes — e o desbloqueio do scaffolding — não depende de mudança
futura da Microsoft. Ver [catálogo de adapters de destino](target-adapter-catalog.md).

## Delegações formais

| Papel | Delegado a | Delegante | Escopo | Data |
| --- | --- | --- | --- | --- |
| _(nenhuma registrada)_ | | | | |

## Efeito sobre o scaffolding

Conforme a seção 9 do runbook, o scaffolding .NET (seção 10) permanece
**bloqueado** até o fechamento de todos os gates vigentes. Com a
substituição do ADR-0004 pelo ADR-0013, o conjunto obrigatório passa a
ser: **ADR-0001 a 0003, ADR-0005 a 0008 e ADR-0013** (emenda de
governança registrada aqui e em `README.md`; o texto da §9 do runbook
v1.0 permanece original até revisão formal do DOCX).
