# ADR-0004 — Aspose.Email como engine primária de escrita/split de PST

- **Status:** proposto _(gate em aberto — ver abaixo)_
- **Data:** 2026-07-20
- **Gate de aprovação:** PoC de biblioteca, licença e jurídico
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** —

## Contexto

A [§18 Seleção da engine PST](../runbook/03-parte-iii-conectores-e-engine-pst.md#18-seleção-da-engine-pst) determina que o produto **não implementará [MS-PST] do zero**: a especificação aberta é referência para investigação e verificação, não justificativa para escrever um parser/writer completo antes de entregar valor. A [§18.1 Decisão recomendada](../runbook/03-parte-iii-conectores-e-engine-pst.md#181-decisão-recomendada) recomenda Aspose.Email for .NET como engine primária (abrir, enumerar, criar, dividir), libpff como validador independente (ADR-0005), ScanPST como fallback de reparo sobre cópia derivada, e **proíbe Outlook/COM** no engine central. A [§10.2](../runbook/02-parte-ii-arquitetura.md#102-gerenciamento-central-de-pacotes) só permite o pacote `Aspose.Email` **após licença**.

## Decisão

Adotar **Aspose.Email for .NET**, com licença OEM/deployment adequada, como engine primária de PST — isolada em `Adapters/Pst.Aspose`, atrás da interface `IPstEngine`; tipos de Aspose nunca atravessam a interface. **Não** implementar parser/writer [MS-PST] próprio. **Não** usar Outlook/COM no engine central.

### Gate em aberto (bloqueia a mudança de status para "aprovado")

Esta decisão permanece `proposto` até que **todos** os itens do gate estejam verdes:

1. **PoC de biblioteca** — Aspose.Email prova fidelidade e escala sobre o corpus PST (abertura, enumeração, criação e split determinístico, incl. cenário de PST grande da §20.3).
2. **Licença** — termos OEM/deployment confirmados para o modelo de execução (workers, redistribuição).
3. **Jurídico** — parecer aprovando licença e uso.

Enquanto o gate não fechar, o pacote `Aspose.Email` **não é adicionado** ao repositório (§10.2, "após licença").

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Implementar [MS-PST] do zero | controle total | anos de edge cases antes de entregar valor | vetado pela §18 |
| Outlook Interop / COM | reuso do Outlook | frágil, não suportado server-side, sem escala | proibido pela §18.1 |
| libpff como engine primária | open source | leitura/verificação apenas; sem writer/splitter robusto | insuficiente para criar/dividir (fica como validador, ADR-0005) |
| Outro SDK comercial | possível fallback | requer nova avaliação | reservado caso o PoC/jurídico do Aspose falhe |

## Consequências

- Positivas: entrega de valor sem reescrever [MS-PST]; dependência de fornecedor isolada no adapter.
- Negativas / dívidas assumidas: custo de licença; dependência comercial; necessidade de manter caminho alternativo se o gate falhar.
- Riscos e mitigação: PoC/jurídico reprova → acionar alternativa comercial e novo ADR; lock-in → interface `IPstEngine` mantém o domínio agnóstico.

## Evidências

Runbook [§18](../runbook/03-parte-iii-conectores-e-engine-pst.md#18-seleção-da-engine-pst), [§18.1](../runbook/03-parte-iii-conectores-e-engine-pst.md#181-decisão-recomendada), [§18.2](../runbook/03-parte-iii-conectores-e-engine-pst.md#182-interface-do-adapter), [§20.3](../runbook/03-parte-iii-conectores-e-engine-pst.md#203-split-simples-por-tamanho-com-aspose). Referência oficial: Aspose PST split/`SplitInto` — Apêndice F. **Pendentes do gate:** relatório de PoC, termos de licença e parecer jurídico anexados ao PR de aprovação.
