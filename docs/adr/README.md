# Registros de Decisão Arquitetural (ADR)

Processo definido pela **seção 9 do runbook**: os ADRs abaixo são criados
neste diretório e aprovados em pull request pelo gate indicado.
**O código do produto só começa após a aprovação de ADR-0001 a ADR-0008.**

## ADRs obrigatórios antes do código

| ADR | Decisão | Gate de aprovação | Status |
| --- | --- | --- | --- |
| ADR-0001 | Monólito modular + workers separados | arquiteto + tech lead | pendente |
| ADR-0002 | .NET 10 LTS e política de atualização | segurança + plataforma | pendente |
| ADR-0003 | Azure SQL + Service Bus Premium | arquitetura + FinOps | pendente |
| ADR-0004 | Aspose como writer/splitter primário | PoC de biblioteca, licença e jurídico | pendente |
| ADR-0005 | libpff somente como verificador independente | compatibilidade e LGPL avaliadas | pendente |
| ADR-0006 | Purview como adapter GA inicial | evidência oficial e teste em tenant controlado | pendente |
| ADR-0007 | Graph FTS bloqueado | reavaliação quando archive/FTS estiverem suportados | pendente |
| ADR-0008 | Modelo de isolamento por tenant/projeto | segurança e DPO | pendente |

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
4. Após o merge, atualize o status na tabela (`pendente` → `aprovado`,
   com link).
5. ADR aprovado é imutável: mudanças exigem um novo ADR que o substitua
   (campo "Substitui / substituído por").
