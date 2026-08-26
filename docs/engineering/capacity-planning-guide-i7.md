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

## 4. Relação com o preflight de produção atual

O caminho de produção existente, `LocalSinglePartExecutionWriter.EnsurePreflightSpace`
(`src/ArchiveBridge.Infrastructure/PstProcessing/LocalSinglePartExecutionWriter.cs`), já falha fechado antes
de qualquer escrita quando o espaço livre do volume de output é insuficiente — mas usa hoje uma checagem
mais simples (`expectedSize + MinFreeSpaceMarginBytes` configurável, via `DriveInfo.AvailableFreeSpace`),
não a fórmula multi-termo do runbook (não soma `repairBackupBytes`/`engineTemporaryOverhead`, porque nenhum
dos dois existe neste caminho hoje, e a margem é um valor de configuração absoluto, não um percentual).

Este Passo **não altera** esse preflight de produção: `ScratchCapacityFormula`/`ScratchCapacityAssessor` são
a implementação de referência, autoritativa e testada da fórmula do runbook, publicada e pronta para ser
adotada por um caminho de produção futuro que precise dos termos adicionais (ex.: quando uma engine de
repair/split for aceita) — mas não estão hoje conectados ao `EnsurePreflightSpace` existente. Documentar
essa distinção explicitamente, em vez de reivindicar uma integração que não existe, é a aplicação direta do
princípio "represente lacuna como zero/não aplicável, nunca esconda" do próprio work order (§4).

## 5. ~24 GB/dia/mailbox — referência, nunca SLA

`ArchiveBridge.Domain.Performance.MailboxGrowthReference.TypicalBytesPerMailboxPerDay` = 24.000.000.000
bytes (24 GB decimais). Citado do runbook §46 (fonte: Microsoft) como taxa TÍPICA — nunca SLA, nunca
critério de aprovação automático (`MailboxGrowthReferenceTests`). `AsReferenceEstimate()` só produz um
`Domain.Performance.SloEvidence.ReferenceEstimate` — nunca um `ObservedMetric` (medição própria) nem um
`ContractualSla` configurado; ver `docs/engineering/slo-evidence-matrix-i7.md`.
