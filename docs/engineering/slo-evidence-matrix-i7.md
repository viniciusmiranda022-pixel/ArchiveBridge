<!-- Evidência executável do work order AB-I7-003 (I7 — Hardening — Passo 2). Fonte de autoridade: docs/runbook/06-parte-vi-plano-desenvolvimento.md §45-47 e docs/engineering/requests/AB-I7-003.md. Distingue, por métrica, ObservedMetric (medição real) de ReferenceEstimate (estimativa/típico do runbook) e ContractualSla (sempre NOT_CONFIGURED neste projeto). -->

# SLO evidence matrix — I7 Passo 2 (AB-I7-003)

**Estado:** evidência de hardening deste Passo. **Não** é uma declaração de Production Ready, SLA comercial
ou meta de capacidade aprovada. Cada linha classifica uma métrica pelo seu `GateStatus`
(`ArchiveBridge.Domain.Performance.SloEvidence.GateStatus`): `Measured` (medição real obtida pelo
`BenchmarkHarness`), `NotMeasured` (não executado neste Passo/ambiente), `NotApplicable` (não se aplica ao
caminho atual) ou `BlockedByExternalDependency` (bloqueado por algo fora do controle deste harness).

`ContractualSla` é **sempre** `NOT_CONFIGURED` nesta tabela — nenhuma métrica abaixo tem uma fonte de SLA
comercial aprovada neste projeto (`Domain.Performance.SloEvidence.ContractualSla.NotConfigured`, o único
construtor público do tipo).

| Métrica | `GateStatus` | Evidência | Fonte/motivo |
| --- | --- | --- | --- |
| `HashStreamingThroughput` (bytes/s) | `Measured` | `ObservedMetric` por execução do harness | `HashStreamingBenchmarkTests` — ver performance-baseline-report-i7.md §2 |
| `PstInspectionThroughput` (bytes/s) | `Measured` | `ObservedMetric` por execução do harness | `PstInspectionBenchmarkTests` |
| `PartitionExecutionThroughput` (bytes/s) | `Measured` | `ObservedMetric` por execução do harness | `PartitionExecutionBenchmarkTests` |
| `MappingCsvGenerationThroughput` (linhas/s, bytes/s) | `Measured` | `ObservedMetric` por execução do harness | `MappingCsvGenerationBenchmarkTests` |
| `PerformanceBenchmarkResultStoreSaveLatency` (ms) | `Measured` (somente quando `ARCHIVEBRIDGE_TEST_SQL` aponta para SQL Server real — CI ou host com container) | `ObservedMetric` por execução do harness | `PerformanceBenchmarkResultStoreTests.BenchmarkingTheStoresOwnSaveLatencyProducesEvidenceThatCanBePersistedAndReplayed` |
| `SqlPartitionExecutionStoreLatency` | `NotMeasured` | — | Exige cadeia de FK (custódia → plano → partes) fora do escopo deste Passo — ver performance-baseline-report-i7.md §3 |
| `SqlPurviewMappingCsvStoreLatency` | `NotMeasured` | — | Mesmo motivo acima |
| `ReconciliationCertificateLatency` | `NotMeasured` | — | Exige `ReconciliationAssessment` persistida primeiro — mesmo motivo acima |
| `PstItemCount` / `PstFolderCount` | `NotApplicable` | — | `HeaderOnlyPstInspectionEngine` nunca percorre a árvore NDB (decisão de ADR do Passo 1); nenhuma engine de contagem foi aceita |
| `HeavyPstRepairSplitThroughput` (100–500+ GB) | `BlockedByExternalDependency` | — | Nenhuma engine de repair/split aceita (STOP-THE-LINE do work order); sparse file sozinho não valida parser PST (runbook §45) |
| `RealTenantEvM365Throughput` | `BlockedByExternalDependency` | — | STOP-THE-LINE do work order: proibido benchmark em tenant/EV/M365 de cliente sem autorização explícita |
| `WorkerProfile.Inspector` (8 vCPU/32 GiB/512 GiB) | `Reference` (não é um `GateStatus` — ver nota) | `ReferenceEstimate` | `WorkerProfileCatalog.Inspector`; runbook §46 |
| `WorkerProfile.HeavyPst` (16–32 vCPU/64–128 GiB/1–2 TiB) | `Reference` | `ReferenceEstimate` | `WorkerProfileCatalog.HeavyPst`; runbook §46 |
| `WorkerProfile.Validator` (4–8 vCPU/16–32 GiB/256–512 GiB) | `Reference` | `ReferenceEstimate` | `WorkerProfileCatalog.Validator`; runbook §46 |
| `WorkerProfile.Upload` (4–8 vCPU/16 GiB/cache mínimo) | `Reference` | `ReferenceEstimate` (scratch sem número — runbook não atribui) | `WorkerProfileCatalog.Upload`; runbook §46 |
| `MailboxGrowthBytesPerDay` (~24 GB/dia) | `Reference` | `ReferenceEstimate` | `MailboxGrowthReference`; runbook §46, fonte Microsoft — **típico, nunca SLA** |
| `ScratchCapacityFormulaSafetyMargin` (20%) | `Reference` | Constante de código (`ScratchCapacityFormula.SafetyMarginPercent`), não uma medição | runbook §46 |

**Nota sobre "Reference":** as linhas de perfil de worker e a taxa de crescimento de mailbox não são
medições do harness — são citações do runbook materializadas como
`Domain.Performance.SloEvidence.ReferenceEstimate`. Elas não usam o enum `GateStatus` porque não
representam o resultado de uma tentativa de medição; `SloEvidenceEntry` (o tipo que amarra
`GateStatus`/`ObservedMetric`/`ReferenceEstimate`/`ContractualSla` para uma métrica medida) usaria
`GateStatus.NotApplicable` com `Reference` preenchido caso esta métrica precisasse aparecer numa entrada
formal — o que este Passo não fez para as quatro linhas de perfil/mailbox, mantendo-as como constantes de
referência simples e diretamente testadas (`WorkerProfileCatalogTests`, `MailboxGrowthReferenceTests`).

## Regressão

Nenhuma linha acima carrega um threshold de aprovação/reprovação. `PerformanceRegressionComparer` (ver
performance-baseline-report-i7.md §4) produz apenas delta informativo entre duas execuções do MESMO
cenário — não existe, hoje, nenhum critério de regressão versionado e aprovado que possa ser aplicado a
qualquer uma destas métricas.
