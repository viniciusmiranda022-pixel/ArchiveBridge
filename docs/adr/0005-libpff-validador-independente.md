# ADR-0005 — libpff somente como verificador independente

- **Status:** proposto
- **Data:** 2026-07-20
- **Gate de aprovação:** compatibilidade e LGPL avaliadas
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** —

## Contexto

A [§18.1](../runbook/03-parte-iii-conectores-e-engine-pst.md#181-decisão-recomendada) posiciona libpff/pffinfo/pffexport como **validador independente**, em container/worker isolado, exclusivamente de leitura e verificação. A [§23 Validação independente](../runbook/03-parte-iii-conectores-e-engine-pst.md#23-validação-independente) exige que a validação combine a engine primária **e** uma engine independente (contagem por folder path, message class, fingerprints amostrados, reabertura após fechamento do writer). A [§7](../runbook/02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades) define o Independent Validator como "segunda engine" que **não altera** o artefato validado.

## Decisão

Usar **libpff** (`pffinfo`/`pffexport`) exclusivamente como **segunda engine de verificação**, em container/worker isolado e somente leitura, para conferência cruzada de contagens, hierarquia de pastas e fingerprints amostrados contra a engine primária (ADR-0004). libpff **não** é usada como writer/splitter. Seus tipos nunca atravessam `IPstEngine`. O status de licença (LGPL) exige avaliação jurídica antes da adoção.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Validação com engine única (só primária) | mais simples | sem verificação independente; cadeia de custódia mais fraca | §23 exige duas engines |
| Usar Aspose para primária e validação | um só fornecedor | não é verificação independente (mesmo código/bugs) | anula o objetivo da segunda engine |
| Outra biblioteca de leitura | possível | requer avaliação de fidelidade e licença | libpff é a recomendada pela §18.1 |

## Consequências

- Positivas: verificação de duas engines real, fortalecendo a cadeia de custódia por item.
- Negativas / dívidas assumidas: necessário avaliar conformidade LGPL (linkagem/uso via processo isolado) e compatibilidade de fidelidade.
- Riscos e mitigação: divergência silenciosa entre engines → tolerâncias só após ADR e corpus de prova (§23.1); questão de licença → parecer jurídico no gate; contenção do uso via container isolado somente-leitura.

## Evidências

Runbook [§18.1](../runbook/03-parte-iii-conectores-e-engine-pst.md#181-decisão-recomendada), [§23](../runbook/03-parte-iii-conectores-e-engine-pst.md#23-validação-independente), [§7](../runbook/02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades). Referência oficial: repositório libpff — Apêndice F. Confirmação do gate: avaliação de compatibilidade e parecer LGPL anexados ao PR.
