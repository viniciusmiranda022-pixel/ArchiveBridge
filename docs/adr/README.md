# Registros de Decisão Arquitetural (ADR)

Processo definido pela **seção 9 do runbook**: os ADRs abaixo são criados
neste diretório e aprovados em pull request pelo gate indicado.
**O código do produto só começa após a aprovação de ADR-0001 a ADR-0008.**

> [!IMPORTANT]
> **O merge de um ADR não equivale à aprovação da decisão.** O fechamento
> de cada gate segue a [matriz de fechamento](gate-closure-matrix.md), que
> separa quatro papéis: **Decision Owner** (decisão final — Vinicius
> Miranda em todos os ADRs), **Revisores necessários** (só quando o tema
> exige competência específica), **Evidence Owner** (produz a evidência) e
> **aprovação formal** (ato do Decision Owner, somente após evidência e
> revisões registradas).

## ADRs obrigatórios antes do código

Todos com status **proposto**; a coluna abaixo lista os **revisores
necessários** por ADR (não um aprovador distinto por documento — ver a
[matriz](gate-closure-matrix.md)). O Decision Owner de todos é Vinicius
Miranda.

| ADR | Decisão | Revisores necessários | Status |
| --- | --- | --- | --- |
| [ADR-0001](0001-monolito-modular-e-workers-isolados.md) | Monólito modular + workers separados | Dev/Tech Lead | **aceito** |
| [ADR-0002](0002-dotnet-10-lts-e-politica-de-atualizacao.md) | .NET 10 LTS e política de atualização | Dev + Segurança | **aceito** |
| [ADR-0003](0003-azure-sql-e-service-bus-premium.md) | Azure SQL + Service Bus Premium | Dev/Cloud ou FinOps | proposto |
| [ADR-0004](0004-aspose-email-engine-primaria.md) | Aspose como writer/splitter primário | — | **substituído** pelo ADR-0013² |
| [ADR-0005](0005-libpff-validador-independente.md) | libpff somente como verificador independente | Jurídico | proposto |
| [ADR-0006](0006-purview-adapter-ga-inicial.md) | Purview como adapter GA inicial | responsável técnico pelo tenant | proposto |
| [ADR-0007](0007-graph-fts-bloqueado.md) | Graph como adapter condicional; rota PST/EV → FTS não habilitada | Segurança/Arquitetura¹ | **aceito** |
| [ADR-0008](0008-isolamento-por-tenant-e-projeto.md) | Modelo de isolamento por tenant/projeto | Segurança/Privacidade | proposto |
| [ADR-0013](0013-exportacao-ev-multiversao.md) | Exportação EV multiversão por capability discovery | Dev + Segurança | **aceito** |

¹ A tabela da seção 9 do runbook descreve o gate do ADR-0007 como
"reavaliação quando archive/FTS estiverem suportados". Isso criaria um
deadlock (o scaffolding nunca começaria sem mudança da Microsoft). A
correção de governança está na [matriz de fechamento](gate-closure-matrix.md):
o que se aprova agora (Arquitetura + Segurança) é a decisão de **manter o
bloqueio**; a disponibilidade futura é gatilho para novo ADR substituto.

² Revisão arquitetural de 2026-07-20 (owner): o Enterprise Vault extrai e
segmenta os PSTs na origem; o Aspose saiu do caminho crítico e o ADR-0004
foi substituído pelo [ADR-0013](0013-exportacao-ev-multiversao.md) antes
de aprovação. O conjunto obrigatório antes do código passa a ser
**0001–0003, 0005–0008 e 0013** (a §9 do runbook v1.0 permanece com o
texto original até revisão formal do DOCX; divergência registrada em
[`docs/ev/README.md`](../ev/README.md)).

## ADRs subsequentes

| ADR | Decisão | Gate de aprovação | Status |
| --- | --- | --- | --- |
| ADR-0009 | Estratégia de fingerprint por item | performance + privacidade | pendente |
| ADR-0010 | Assinatura de evidência e WORM | jurídico + segurança | pendente |
| ADR-0011 | Portal React ou Blazor | time responsável | pendente |
| ADR-0012 | Single-region com DR ou active/passive | negócio + custo | pendente |

## Como criar um ADR

1. Copie `0000-template.md` para `NNNN-titulo-curto.md` (numeração
   sequencial, quatro dígitos).
2. Preencha todas as seções; decisões sem alternativas consideradas ou sem
   consequências explícitas são devolvidas.
3. Abra PR referenciando a linha correspondente da tabela acima e indique
   os revisores necessários (competência específica, quando o tema exigir).
4. A mudança de status para `aceito` registra no ADR: Decision Owner e
   data, revisores e pareceres, evidência e o Evidence Owner que a produziu,
   e eventuais condições. O flip ocorre **no mesmo PR que anexa a
   evidência** ou, quando a evidência **já está no `main`**, em um **PR de
   aceite separado** que a referencie explicitamente — conforme a
   [matriz de fechamento](gate-closure-matrix.md).
5. ADR aceito é imutável: mudanças exigem um novo ADR que o substitua
   (campo "Substitui / substituído por").
