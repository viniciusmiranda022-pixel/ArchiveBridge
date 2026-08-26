<!-- Evidência executável do work order AB-I7-003 (I7 — Hardening — Passo 2: Performance, Capacity & SLO Baseline). Fonte de autoridade: docs/runbook/06-parte-vi-plano-desenvolvimento.md §45 (Test Strategy), §46 (Performance e dimensionamento), §47 (Production Readiness) e docs/engineering/requests/AB-I7-003.md. Este documento NÃO declara I7 completo, NÃO declara Production Ready, NÃO estabelece SLA comercial e NÃO inventa meta numérica ausente da fonte de autoridade. -->

# Performance baseline report — I7 Passo 2 (AB-I7-003)

**Autoridade:** este documento não substitui nem contradiz o runbook nem nenhum ADR aceito; descreve o
harness de benchmark que o CÓDIGO hoje realmente implementa e os resultados que ele produz. Uma
estimativa/referência citada do runbook permanece explicitamente marcada como estimativa — nunca vira
threshold rígido, SLA ou mínimo garantido por inferência deste documento.

**STOP-THE-LINE (herdado do work order):** este Passo não autoriza declarar I7 completo ou Production
Ready, não inicia I8/canário/go-live, não transforma estimativa do runbook em SLA, não executa benchmark em
tenant/EV/M365 de cliente, não introduz dependência obrigatória de Azure PaaS, não declara pen-test
concluído e não relaxa nenhum limite de segurança para melhorar um número de benchmark.

## 1. O harness

`ArchiveBridge.Application.Performance.BenchmarkHarness` (`src/ArchiveBridge.Application/Performance/BenchmarkHarness.cs`)
executa um workload por N iterações medidas após um aquecimento descartado, registrando por execução:

- versão do build, descrição do runtime, perfil do host (`BenchmarkHarness.RunAsync` — parâmetros
  `buildVersion`/`runtimeDescription`/`hostProfile`, nunca inferidos automaticamente do ambiente: quem
  invoca o harness declara explicitamente o que está medindo);
- dataset sintético (`Domain.Performance.BenchmarkDatasetDescriptor` — nome, tamanho, contagem de itens,
  seed; o Domain recusa rótulos que pareçam caminho real/endereço, ver `BenchmarkDatasetDescriptorTests`);
- warmup/iterações;
- timestamp UTC da execução (`IClock` injetado, nunca `DateTime.Now`).

Por iteração medida, coleta wall-clock, tempo de CPU do processo (`Process.TotalProcessorTime`), peak
working set (`Process.PeakWorkingSet64`), bytes/itens processados (quando o cenário os expõe) e o desfecho
(`Success`/`Error`/`Cancelled`/`ResourceLimit`) — nunca omite uma iteração por erro: uma iteração que lança
é registrada como `Error`, sanitizada (sem mensagem/stack trace), e as demais continuam
(`Domain.Performance.BenchmarkMeasurement`, `PerformanceBenchmarkRunRecordTests.AnIterationThatErroredIsStillRecordedNeverSilentlyDropped`).
O resultado completo é `Domain.Performance.PerformanceBenchmarkRunRecord` — imutável, versionado
(`SchemaVersion`), fail-closed sobre inconsistência estrutural (índice de iteração faltando/duplicado —
`PerformanceBenchmarkRunRecordTests`).

## 2. Cenários implementados (Measured)

Cobrem os caminhos JÁ IMPLEMENTADOS e testáveis sem ambiente externo de cliente, conforme escopo obrigatório
do work order §1:

| Cenário | O que mede | Datasets (classes) | Depende de SQL Server? | Testes |
| --- | --- | --- | --- | --- |
| `HashStreaming` | Hash/streaming SHA-256 de um artefato (mesmo padrão `IncrementalHash` de `LocalSinglePartExecutionWriter.CopySourceToStagingAsync`) | 4 KiB, 256 KiB, 4 MiB (fronteira do buffer de streaming interno) | Não | `HashStreamingBenchmarkTests` |
| `PstInspection` | `HeaderOnlyPstInspectionEngine.InspectAsync` sobre cabeçalho PST sintético válido (custódia em memória, sem SQL) | 4 KiB, 1 MiB | Não | `PstInspectionBenchmarkTests` |
| `PartitionExecution` | `LocalSinglePartExecutionWriter.ExecuteAsync` (único caminho aceito, `SinglePartWithinTarget`) — cópia byte-for-byte real, um plano/parte novo por iteração (nunca mede o atalho de réplay idempotente) | 4 KiB, 256 KiB | Não | `PartitionExecutionBenchmarkTests` |
| `MappingCsvGeneration` | `PurviewMappingCsvGenerator.Generate` (serviço puro) sobre linhas sintéticas | 10, 100, 500 linhas (500 = `MappingSchema.MaxDataRows`, fronteira) | Não | `MappingCsvGenerationBenchmarkTests` |
| `PerformanceBenchmarkResultStoreSave` | Latência de `SqlPerformanceBenchmarkResultStore.SaveAsync` contra SQL Server real, mais o round-trip de persistência/replay da própria evidência | 1 medição por execução sintética | **Sim** (container SQL Server do CI) | `PerformanceBenchmarkResultStoreTests.BenchmarkingTheStoresOwnSaveLatencyProducesEvidenceThatCanBePersistedAndReplayed` |

Todos os datasets são sintéticos e determinísticos (conteúdo derivado apenas da seed, nunca dado real);
nenhum resultado carrega assunto, corpo, destinatário, anexo, mailbox real ou caminho de arquivo real —
`BenchmarkMeasurement` só expõe campos numéricos/enum por construção (ver
`MappingCsvGenerationBenchmarkTests.ResultsNeverCarryRealMailboxOrFilePathTextOnlyAggregatedCounts`).

## 3. Cenários explicitamente NÃO medidos neste Passo (NotMeasured / BlockedByEnvironment)

Marcados assim de propósito — nunca aprovados implicitamente (work order §7/§9, STOP-THE-LINE):

- **Latência SQL das stores mais profundas do pipeline** (`SqlPartitionExecutionStore`,
  `SqlPurviewMappingCsvStore`, `SqlReconciliationCertificateStore`): cada uma exige uma cadeia de
  pré-requisitos com FK real (custódia → plano/partes → execução; ou avaliação de reconciliação →
  disposition → certificate) — persistir um registro isolado violaria a integridade referencial do schema.
  Medir estas stores exige orquestrar a cadeia completa via os use cases reais (`PlanPstPartitionUseCase`,
  `ExecutePartitionPlanUseCase` etc.), o que este Passo não fez para manter o escopo desta mudança de
  hardening contido; a store MAIS SIMPLES do pipeline de benchmark (a própria
  `SqlPerformanceBenchmarkResultStore`, sem dependência de FK externa) foi medida como evidência de latência
  SQL representativa (§2). `GateStatus.NotMeasured` — motivo: "cadeia de FK do pipeline fora do escopo deste
  Passo; ver §3 do performance baseline report".
- **Reconciliation/certificate em datasets sintéticos:** mesma razão de cadeia de FK acima
  (`ReconciliationCertificate` exige uma `ReconciliationAssessment` persistida). `GateStatus.NotMeasured`.
- **PSTs de 100–500+ GB (repair/split, perfil Heavy PST):** nenhuma engine de repair/split está aceita
  (work order STOP-THE-LINE: "não introduzir engines/connectors novos fora do roadmap já aceito"); o
  runbook é explícito que um sparse file sozinho não valida parser PST — simulá-lo aqui produziria uma
  evidência enganosa. `GateStatus.BlockedByExternalDependency` — motivo: "nenhuma engine de repair/split
  aceita; CI não executa bytes reais suficientes para esta classe".
- **Benchmark em tenant/EV/M365 de cliente real:** fora de escopo por STOP-THE-LINE explícito do work
  order. `GateStatus.BlockedByExternalDependency`.
- **Contagem de itens/pastas de um PST (percorrer a árvore NDB):** `HeaderOnlyPstInspectionEngine` nunca
  percorre a árvore NDB por decisão de ADR do Passo 1 (`ItemCount`/`FolderCount` sempre `null`) — não há o
  que medir sem uma engine que ainda não foi aceita. `GateStatus.NotApplicable`.

Ver a matriz completa (todas as métricas, incluindo as referências do runbook) em
`docs/engineering/slo-evidence-matrix-i7.md`.

## 4. Comparação de regressão

`ArchiveBridge.Application.Performance.PerformanceRegressionComparer.Compare` produz um delta determinístico
(`MeanWallClockMs`, `ErrorRatePercent`, e `MeanBytesPerSecond`/`MeanItemsPerSecond` quando aplicáveis) entre
um baseline e uma execução atual do MESMO cenário. Carrega sempre o aviso fixo
`PerformanceRegressionComparer.InformativeOnlyNotice` — não existe hoje nenhum critério de regressão
versionado e aprovado no repositório, então o resultado nunca promove nem falha CI automaticamente (work
order §6). Quando um critério futuro for aprovado, ele deve ser aplicado por cima deste relatório (fora
deste tipo), nunca embutido como valor mágico.

## 5. Como reproduzir localmente / em host dedicado

Cenários que NÃO dependem de SQL Server (rodam em qualquer máquina com o SDK .NET 10 instalado, sem
infraestrutura externa):

```bash
dotnet test tests/ArchiveBridge.Application.Tests/ArchiveBridge.Application.Tests.csproj \
    --filter "FullyQualifiedName~Performance" -c Release

dotnet test tests/ArchiveBridge.Integration.Tests/ArchiveBridge.Integration.Tests.csproj \
    --filter "FullyQualifiedName~Performance.HashStreamingBenchmarkTests|FullyQualifiedName~Performance.PstInspectionBenchmarkTests|FullyQualifiedName~Performance.PartitionExecutionBenchmarkTests" \
    -c Release
```

Cenário que depende de SQL Server real (mesmo container usado pelo `dotnet` job do CI — ver
`.github/workflows/ci.yml`): defina `ARCHIVEBRIDGE_TEST_SQL` apontando para uma instância SQL Server de
teste e rode:

```bash
export ARCHIVEBRIDGE_TEST_SQL="Server=localhost,11433;User ID=sa;Password=...;TrustServerCertificate=True;Encrypt=False"
dotnet test tests/ArchiveBridge.Integration.Tests/ArchiveBridge.Integration.Tests.csproj \
    --filter "FullyQualifiedName~Performance.PerformanceBenchmarkResultStoreTests" -c Release
```

Em um host dedicado (ex.: perfil `Inspector`/`Validator` de `docs/engineering/capacity-planning-guide-i7.md`),
os mesmos comandos se aplicam — o `hostProfile` passado ao harness em cada teste (`"local-sandbox"`/
`"unit-test"`/`"ci-sql-container"`) é apenas um rótulo textual; para uma execução em host real, edite o
literal do teste (ou promova-o a parâmetro, fora deste Passo) para identificar o perfil real usado, mantendo
a distinção "medido neste host" nunca implícita.

## 6. Resultados observados nesta execução

Este Passo não fixa números absolutos neste relatório como baseline "aprovado": o harness produz evidência
reproduzível por execução (`PerformanceBenchmarkRunRecord`), mas nenhum número aqui é uma meta ou promessa —
ver `docs/engineering/slo-evidence-matrix-i7.md` para a distinção formal entre medido/estimado/não
medido/bloqueado por métrica. Um número observado em CI compartilhado (host não dedicado, carga variável) é
apenas evidência informativa daquela execução, nunca um SLO.
