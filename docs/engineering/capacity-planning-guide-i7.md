<!-- Evidência executável do work order AB-I7-003 (I7 — Hardening — Passo 2). Fonte de autoridade: docs/runbook/06-parte-vi-plano-desenvolvimento.md §46 (Performance e dimensionamento). Perfis de worker e a fórmula de scratch são citados como ESTIMATIVA/referência do runbook — nunca mínimo garantido nem SLA. -->

# Capacity planning guide — I7 Passo 2 (AB-I7-003)

**Autoridade:** este guia materializa em código, com testes, os perfis de dimensionamento e a fórmula de
scratch já descritos no runbook §46 — não inventa nenhum número ausente da fonte, e preserva explicitamente
a natureza de estimativa de tudo que o runbook já marca como tal.

## 1. Perfis de worker (referência, não mínimo garantido)

`ArchiveBridge.Domain.Performance.WorkerProfileCatalog` (`src/ArchiveBridge.Domain/Performance/WorkerProfileReference.cs`)
materializa os quatro perfis do runbook §46 como constantes versionadas:

| Perfil | vCPU | RAM | Scratch | Uso típico |
| --- | --- | --- | --- | --- |
| Inspector | 8 | 32 GiB | 512 GiB | PSTs até ~100 GB |
| Heavy PST | 16–32 | 64–128 GiB | 1–2 TiB NVMe/SSD | 100–500+ GB, repair/split |
| Validator | 4–8 | 16–32 GiB | 256–512 GiB | scan/hash independente |
| Upload | 4–8 | 16 GiB | *(runbook não atribui bytes — "cache mínimo")* | AzCopy/rede |

Todo `WorkerProfileReference` carrega `WorkerProfileReference.ReferenceNotice`, fixo: "Estimativa de
referência do runbook — NÃO é mínimo garantido nem SLA". `Upload.MinScratchBytes`/`MaxScratchBytes` são
`null` — o runbook não atribui um número a "cache mínimo"; o catálogo nunca inventa um valor para preencher
a lacuna (`WorkerProfileCatalogTests.UploadHasNoFabricatedScratchNumberBecauseTheRunbookGivesNone`).

Estes valores são testados byte-a-byte contra a tabela do runbook (`WorkerProfileCatalogTests`) — uma
divergência futura entre código e runbook quebra o teste em vez de passar silenciosamente.

## 2. Fórmula de scratch

`ArchiveBridge.Domain.Performance.ScratchCapacityFormula` implementa exatamente a fórmula do runbook §46:

```
requiredScratchBytes =
    sourceCopyBytes + expectedPartBytes + repairBackupBytes + engineTemporaryOverhead
    + safetyMargin(20%)
```

Propriedades garantidas e testadas (`ScratchCapacityFormulaTests`):

- **Aritmética inteira, nunca ponto flutuante** — a margem de 20% é uma divisão de teto
  (`ceil(base × 20 / 100)`), sempre arredondada PARA CIMA, nunca para baixo (nunca subestima o requisito por
  truncamento).
- **Fail-closed sobre negativo:** qualquer termo negativo (unidade/medição ambígua) recusa calcular
  (`ScratchCapacityFormulaError.NegativeInput`), sem lançar exceção — o chamador decide o que fazer.
- **Fail-closed sobre overflow:** soma ou multiplicação que excederia `long.MaxValue` recusa calcular
  (`ScratchCapacityFormulaError.Overflow`) em vez de transbordar silenciosamente.
- **Termo ainda não implementado ⇒ zero explícito, nunca omitido:** `repairBackupBytes` é hoje sempre `0`
  neste repositório porque nenhuma engine de repair/split foi aceita — participa explicitamente da soma como
  zero (mesmo resultado que omiti-lo), nunca escondido do chamador.

## 3. Avaliação de orçamento — `Unknown` nunca vira `Enough`

`ArchiveBridge.Domain.Performance.ScratchCapacityAssessor.Assess(requiredScratchBytes, availableScratchBytes)`
compara o requisito calculado contra a capacidade observada:

| `availableScratchBytes` | `CapacityBudgetOutcome` |
| --- | --- |
| `null` (não medido) | `Unknown` — **nunca** `Enough` |
| negativo (ambíguo) | `Unknown` — **nunca** `Enough` |
| `>= required` | `Enough` |
| `< required` | `Insufficient` |

Este é o invariante central do work order §4: nenhuma execução pode converter `Unknown` de capacidade em
`Enough` por default. Testado exaustivamente em `ScratchCapacityAssessorTests`, incluindo a fronteira exata
(`available == required` ⇒ `Enough`) e `available == required - 1` ⇒ `Insufficient`.

## 4. Relação com o preflight de produção atual (AB-I7-004: agora CONECTADO)

O caminho de produção existente, `LocalSinglePartExecutionWriter.EnsurePreflightSpace`
(`src/ArchiveBridge.Infrastructure/PstProcessing/LocalSinglePartExecutionWriter.cs`), agora usa
`ScratchCapacityFormula`/`ScratchCapacityAssessor` como autoridade ÚNICA de requisito de espaço — o
Engineering Reviewer (AB-I7-004, blocker 2) apontou que a versão anterior deste Passo publicava a fórmula
sem religá-la ao único caminho de produção que já conhece o footprint necessário, deixando duas regras
concorrentes. Essa lacuna está fechada.

Mapeamento dos termos da fórmula para o ÚNICO caso que este writer materializa (`SinglePartWithinTarget`,
cópia byte-for-byte para o volume de OUTPUT):

| Termo da fórmula | Valor neste caminho | Prova |
| --- | --- | --- |
| `sourceCopyBytes` | `0` | A origem permanece intacta no volume de ORIGEM (`PstStorageOptions.RootPath`), nunca tocado por este preflight — só o volume de OUTPUT é checado; não existe uma segunda cópia da origem além da própria parte. |
| `expectedPartBytes` | `expectedSize` | O único byte realmente escrito no volume de output (o arquivo de staging que se torna a parte final via `Directory.Move`). |
| `repairBackupBytes` | `0` | Nenhuma engine de repair/split é usada por este writer — o ÚNICO caso autorizado é `SinglePartWithinTarget` (cópia byte-for-byte da origem inteira); nenhuma engine nova introduzida por este Passo. |
| `engineTemporaryOverhead` | `0` | Os sidecars (`part.sha256` + `manifest.json`) somam poucos bytes/KB — desprezíveis frente à própria margem de 20%; contabilizá-los separadamente inventaria um número que o runbook não fornece. |

A margem fixa historicamente configurada (`PartitionExecutionOutputOptions.MinFreeSpaceMarginBytes`) é
preservada como um PISO adicional — nunca dupla contagem semântica (representa uma margem operacional
distinta, sobre o mesmo `expectedSize`), mas o requisito final é o MAIOR entre o valor legado
(`expectedSize + MinFreeSpaceMarginBytes`) e o valor calculado pela fórmula: esta integração nunca reduz a
proteção que já existia antes dela. Overflow em qualquer um dos dois cálculos, entrada negativa, ou
incapacidade de determinar o espaço disponível (raiz de volume irresolvível/drive não pronto — via o seam
interno `IScratchSpaceProbe`) falham fechado com `PartitionExecutionLimitExceededException("INSUFFICIENT_SPACE")`,
antes de qualquer diretório de staging ser criado — `Unknown` nunca vira `Enough`. Provado em
`LocalSinglePartExecutionWriterPreflightCapacityTests`: espaço exatamente suficiente materializa o output
normalmente, 1 byte abaixo do requisito falha fechado sem nenhum efeito no disco, capacidade indeterminável
falha fechado, e overflow aritmético na combinação com a margem legada também falha fechado.

Os termos `repairBackupBytes`/`engineTemporaryOverhead` continuam `0` porque nenhum dos dois existe neste
caminho hoje — quando uma engine de repair/split ou um perfil de worker com overhead temporário real for
aceito em um Passo futuro, esses termos passam a ser preenchidos com valores reais, nunca inventados agora.

## 5. ~24 GB/dia/mailbox — referência, nunca SLA

`ArchiveBridge.Domain.Performance.MailboxGrowthReference.TypicalBytesPerMailboxPerDay` = 24.000.000.000
bytes (24 GB decimais). Citado do runbook §46 (fonte: Microsoft) como taxa TÍPICA — nunca SLA, nunca
critério de aprovação automático (`MailboxGrowthReferenceTests`). `AsReferenceEstimate()` só produz um
`Domain.Performance.SloEvidence.ReferenceEstimate` — nunca um `ObservedMetric` (medição própria) nem um
`ContractualSla` configurado; ver `docs/engineering/slo-evidence-matrix-i7.md`.
