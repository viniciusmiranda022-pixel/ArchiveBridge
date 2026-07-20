# Registros de Decisão Arquitetural (ADR)

Processo definido pela **seção 9 do runbook**: os ADRs abaixo são criados
neste diretório e aprovados em pull request pelo gate indicado.
**O código do produto só começa após a aprovação de ADR-0001 a ADR-0008.**

> [!IMPORTANT]
> **O merge de um ADR não equivale à aprovação da decisão.** O fechamento
> de cada gate segue a [matriz de fechamento](gate-closure-matrix.md):
> aprovador real, data, evidência/parecer e ressalvas registrados no ADR —
> sem acúmulo automático de papéis e sem delegação informal.

## ADRs obrigatórios antes do código

Todos redigidos com status **proposto**, fiéis ao runbook; a aprovação formal
cabe ao gate indicado (ver "Como criar um ADR", passo 4).

| ADR | Decisão | Gate de aprovação | Status |
| --- | --- | --- | --- |
| [ADR-0001](0001-monolito-modular-e-workers-isolados.md) | Monólito modular + workers separados | arquiteto + tech lead | proposto |
| [ADR-0002](0002-dotnet-10-lts-e-politica-de-atualizacao.md) | .NET 10 LTS e política de atualização | segurança + plataforma | proposto |
| [ADR-0003](0003-azure-sql-e-service-bus-premium.md) | Azure SQL + Service Bus Premium | arquitetura + FinOps | proposto |
| [ADR-0004](0004-aspose-email-engine-primaria.md) | Aspose como writer/splitter primário | PoC de biblioteca, licença e jurídico | proposto (gate em aberto) |
| [ADR-0005](0005-libpff-validador-independente.md) | libpff somente como verificador independente | compatibilidade e LGPL avaliadas | proposto |
| [ADR-0006](0006-purview-adapter-ga-inicial.md) | Purview como adapter GA inicial | evidência oficial e teste em tenant controlado | proposto |
| [ADR-0007](0007-graph-fts-bloqueado.md) | Graph FTS bloqueado | reavaliação quando archive/FTS estiverem suportados | proposto |
| [ADR-0008](0008-isolamento-por-tenant-e-projeto.md) | Modelo de isolamento por tenant/projeto | segurança e DPO | proposto |

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
3. Abra PR referenciando a linha correspondente da tabela acima e marque o
   gate de aprovação como revisor.
4. A mudança de status para `aprovado` só ocorre no PR que anexa a
   evidência do gate, registrando no ADR: aprovador real, data,
   evidência/parecer e eventuais condições — conforme a
   [matriz de fechamento](gate-closure-matrix.md).
5. ADR aprovado é imutável: mudanças exigem um novo ADR que o substitua
   (campo "Substitui / substituído por").
