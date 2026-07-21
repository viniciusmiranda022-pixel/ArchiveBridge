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
| [0004](0004-aspose-email-engine-primaria.md) | — (não mais aplicável) | — | — | **substituído** pelo [ADR-0013](0013-exportacao-ev-multiversao.md) em 2026-07-20, antes de aprovação |
| [0005](0005-libpff-validador-independente.md) | Compatibilidade + LGPL | A definir | teste de compatibilidade e parecer jurídico | pendente |
| [0006](0006-purview-adapter-ga-inicial.md) | Evidência Microsoft + tenant controlado | A definir | relatório de validação em tenant | pendente |
| [0007](0007-graph-fts-bloqueado.md) | Arquitetura + Segurança | A definir | análise da documentação Microsoft confirmando que o cenário PST/FTS não está aprovado | pendente |
| [0008](0008-isolamento-por-tenant-e-projeto.md) | Segurança + DPO | A definir | threat model e avaliação de dados | pendente |
| [0013](0013-exportacao-ev-multiversao.md) | Arquitetura + Segurança | A definir | parecer sobre o ADR, o contrato `IEvExportAdapter` e a especificação de capability discovery | pendente |

Legenda de estado:

- **pendente** — aguarda o responsável exercer o gate e anexar evidência;
- **substituído** — ADR retirado antes de aprovação por decisão registrada;
  o gate correspondente deixa de existir e o ADR substituto entra na
  matriz com gate próprio.

### ADR-0007: decisão aprovável agora vs. capability bloqueada

O que se aprova no ADR-0007 é a **decisão de manter o Graph FTS bloqueado
e fora do primeiro release** — decisão aprovável hoje por Arquitetura +
Segurança, com base na documentação Microsoft atual. A **capability**
`GraphFtsArchiveImport` permanece `BLOCKED` (bloqueada por design). A
futura disponibilidade do Graph FTS **não é o gate deste ADR**: é o
gatilho para um **novo ADR substituto** (condições da §28.3). Assim, o
fechamento dos oito gates — e o desbloqueio do scaffolding — não depende
de mudança futura da Microsoft.

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
