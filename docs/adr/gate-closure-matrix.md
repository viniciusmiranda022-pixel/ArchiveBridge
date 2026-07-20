# Matriz de fechamento dos gates de aprovação (ADR-0001 a ADR-0008)

Princípio de governança: **o merge dos documentos ADR não equivale à
aprovação das decisões.** Cada gate é fechado por um responsável real, com
evidência, e ninguém acumula automaticamente os papéis de arquiteto,
segurança, plataforma, FinOps, jurídico e DPO — salvo **delegação formal**
registrada nesta matriz (quem delegou, para quem, escopo e data).

## Registro obrigatório de cada aprovação

Ao fechar um gate, o ADR correspondente deve registrar:

1. **Aprovador real** (nome/identidade de quem exerceu o papel);
2. **Data da aprovação**;
3. **Evidência ou parecer** (link para documento, relatório, contrato ou PR);
4. **Condições e ressalvas**, se houver.

Só então o `Status` do ADR muda de `proposto` para `aprovado` e a tabela de
`README.md` é atualizada — no mesmo PR que anexa a evidência.

## Matriz

| ADR | Gate | Responsável | Evidência exigida | Estado |
| --- | --- | --- | --- | --- |
| [0001](0001-monolito-modular-e-workers-isolados.md) | Arquiteto + Tech Lead | A definir | parecer técnico | pendente |
| [0002](0002-dotnet-10-lts-e-politica-de-atualizacao.md) | Segurança + Plataforma | A definir | parecer de runtime e patching | pendente |
| [0003](0003-azure-sql-e-service-bus-premium.md) | Arquitetura + FinOps | A definir | estimativa de custos | pendente |
| [0004](0004-aspose-email-engine-primaria.md) | PoC + Licença + Jurídico | A definir | relatório de PoC ([plano](0004-poc-plan.md)), contrato e parecer | **bloqueado** |
| [0005](0005-libpff-validador-independente.md) | Compatibilidade + LGPL | A definir | teste de compatibilidade e parecer jurídico | pendente |
| [0006](0006-purview-adapter-ga-inicial.md) | Evidência Microsoft + tenant controlado | A definir | relatório de validação em tenant | pendente |
| [0007](0007-graph-fts-bloqueado.md) | Reavaliação futura | A definir | capability evidence (condições da §28.3) | **bloqueado por design** |
| [0008](0008-isolamento-por-tenant-e-projeto.md) | Segurança + DPO | A definir | threat model e avaliação de dados | pendente |

Legenda de estado:

- **pendente** — aguarda o responsável exercer o gate e anexar evidência;
- **bloqueado** — depende de artefatos externos ainda inexistentes
  (ADR-0004: relatório de PoC executado, contrato de licença, parecer
  jurídico);
- **bloqueado por design** — o próprio ADR determina que só será
  reavaliado quando condições externas mudarem (ADR-0007: §28.3 do
  runbook).

## Delegações formais

| Papel | Delegado a | Delegante | Escopo | Data |
| --- | --- | --- | --- | --- |
| _(nenhuma registrada)_ | | | | |

## Efeito sobre o scaffolding

Conforme a seção 9 do runbook, o scaffolding .NET (seção 10) permanece
**bloqueado** até que os oito gates estejam fechados — incluindo o
ADR-0004, que depende dos três artefatos externos.
