# Vertical Slice 4B — Partition Execution Foundation (Passo 3)

## Status

**Em desenvolvimento — PR deve permanecer em Draft até `ARCHIVEBRIDGE_MERGE_APPROVED` explícito do
Engineering Reviewer para o HEAD SHA corrente, com CI totalmente verde.**

Work order versionado: [`docs/engineering/requests/AB-4B-006.md`](requests/AB-4B-006.md) (`REQUEST_ID: AB-4B-006`).
Passo anterior (mergeado): [Partition Planning](vertical-slice-04b-partition-planning.md).

## Objetivo

Executar, de forma segura e reiniciável, **apenas** o único caso já provado pelo planner do Passo 2 —
`Planned / SinglePartWithinTarget` — materializando a parte planejada como um output IMUTÁVEL com SHA-256,
manifesto durável, lineage/custódia e checkpoints de restart. Nenhum parser/writer/vendor novo, nenhum split
multi-part real, nenhuma semantic partitioning (ver [Fora do escopo](#fora-do-escopo--stop-the-line)).

Executar é **materialização verificada de uma cópia byte-for-byte**, não split: para `SinglePartWithinTarget`
o artefato inteiro cabe em uma parte, então a única operação segura e honesta é copiar a origem para um
output controlado, hasheá-la de forma independente e só então publicar a evidência.

## Elegibilidade: só o caso já provado pelo planner

`ExecutePartitionPlanUseCase` recusa, ANTES de qualquer I/O e sem qualquer efeito externo (nem arquivo, nem
linha de evidência), qualquer plano que não seja:

- `Outcome == Planned` **e** `Reason == SinglePartWithinTarget`;
- canônico (`PartitionPlan.IsCanonical`) e com identidade determinística autoconsistente
  (`PartitionPlan.HasConsistentIdentity()`);
- exatamente **uma** parte, cobrindo a origem inteira (`CoversEntireSource`).

`Unsupported`/`Blocked`, planos não canônicos e identidades forjadas lançam
`PartitionExecutionNotEligibleException` — provado por `AnUnsupportedPlanIsRejectedWithZeroOutputAndZeroExecutionRows`,
`ABlockedPlanIsRejectedWithZeroOutputAndZeroExecutionRows`, `ForgedPlanIdentityIsRejectedBeforeAnyIO`.

## Invariante central: output byte-for-byte idêntico à origem

Como `SinglePartWithinTarget` sempre cobre o artefato inteiro, o Domain reforça — independentemente da
implementação de Infrastructure — que `OutputHash == SourceHash` **e** `OutputSizeBytes == SourceSizeBytes`
em `PartitionExecutionRecord.Complete`/`Rehydrate`. O mesmo invariante é travado no banco
(`CK_pst_partition_executions_byte_identical`). Duas camadas independentes, nenhuma confia cegamente na
outra (mesmo padrão de defesa em profundidade do Passo 2).

## Protocolo de materialização (checkpoints de crash-safety)

`LocalSinglePartExecutionWriter` segue o MESMO protocolo recuperável de duas fases já hardenado em
`FileSystemMappingArtifactStore` (Slice 6), adaptado para streaming (a origem pode ter até
`PartitionPolicy.RunbookHardPartBytes` — não cabe em memória):

```text
1. output final (caminho determinístico, IDs opacos) já existe?
     sim → reabre/reconfere hash+tamanho contra o esperado do plano
             confere  → devolve (réplay/convergência idempotente)
             diverge  → PartitionExecutionOutputTamperedException (NUNCA sobrescreve)
     não → segue para 2

2. preflight de espaço em disco na raiz de output (margem configurável)
     insuficiente → PartitionExecutionLimitExceededException("INSUFFICIENT_SPACE")

3. copia a origem (read-only, contenção dupla anti-symlink) → arquivo de staging NOVO
   (nome aleatório por tentativa), hasheando em streaming (SHA-256 incremental)
     origem mudou (tamanho/hash) → PartitionExecutionSourceStaleException

   ─── CHECKPOINT 1 ───  flush + fsync do staged file, REABRE e reconfere hash/tamanho
                          independentemente do que foi calculado durante a escrita
     não confere → PartitionExecutionOutputTamperedException (staging descartado)

4. grava sidecars (part.sha256, manifest.json) no MESMO diretório de staging

   ─── CHECKPOINT 2 ───  Directory.Move ATÔMICO do bundle inteiro (part.pst + sha256 +
                          manifest.json) para o caminho final
     corrida perdida (final já existe) → converge para o resultado do vencedor (passo 1)

   REABRE e reconfere o bundle FINAL (mesma verificação do passo 1) antes de devolver sucesso

5. Application persiste o manifesto SQL (append-only, INSERT único)

   ─── CHECKPOINT 3 ───  índice único (tenant, projeto, plano, parte) é o backstop de
                          concorrência; conflito ⇒ relê o canônico já persistido
```

Crash em qualquer ponto ANTES do checkpoint 3 nunca deixa uma linha SQL órfã (não há nada para desfazer: o
INSERT só acontece depois de tudo verificado). Crash DEPOIS do checkpoint 2 mas antes do 3 deixa o output
final já publicado no filesystem — o restart (nova chamada ao caso de uso) detecta o bundle existente no
passo 1, reconfere e converge sem reescrever, então persiste o checkpoint 3 que faltava
(`ACrashAfterFinalizationButBeforeThePersistCheckpointConvergesWithoutDuplicating`).

## Idempotência, concorrência e órfãos

- **Réplay barato**: uma execução canônica já persistida (checkpoint 3) é devolvida direto pela consulta SQL
  — o writer NUNCA é reinvocado, nenhum arquivo é reaberto (`IdempotentReplayReturnsTheSameCanonicalExecutionWithoutRewritingTheOutput`).
  Este é o caminho rápido; a reconferência do passo 1 do protocolo acima só entra em jogo quando o checkpoint
  3 ainda não existe (crash-recovery), nunca no réplay comum.
- **Concorrência**: 6 execuções simultâneas do mesmo plano convergem para exatamente uma linha canônica e um
  único arquivo final, byte-for-byte idêntico à origem
  (`ConcurrentExecutionOfTheSamePlanConvergesToExactlyOneCanonicalResult`). O perdedor da corrida de INSERT
  relê o canônico; releitura vazia ⇒ `PartitionExecutionConflictUnresolvedException` (nunca devolve execução
  não persistida).
- **Staging órfão nunca é confundido com sucesso**: a canonicidade é decidida SOMENTE pelo bundle validado no
  caminho final — um diretório de staging abandonado (crash real, sem exception handling algum) é
  completamente inerte para qualquer execução subsequente
  (`AnOrphanStagingDirectoryIsNeverConfusedWithACompletedPartAndIsSafelyReconciled`). `TempStagingReconciler`
  é uma operação de MANUTENÇÃO explícita (nunca automática no caminho de execução) que remove staging mais
  antigo que um limiar de idade configurável — seguro porque uma cópia legítima em andamento continua
  atualizando o arquivo a cada chunk.
- **Adulteração nunca é sobrescrita**: um output existente que diverge do hash/tamanho esperados falha
  fechado na reabertura (`TamperingTheExistingOutputIsDetectedOnReadbackAndNeverSilentlyOverwritten`) — a
  disposição exige decisão fora deste caminho de código (mesmo princípio do runbook §20.5: um part perdido
  nunca é regenerado silenciosamente).

## Arquitetura do slice

```text
Application.PstProcessing
  ExecutePartitionPlanUseCase
     │
     ├── IPartitionPlanStore.FindByIdAsync(scope, planId)     ─── anti-IDOR: NotFound indistinguível
     ├── EnsureEligible(plan)                                 ─── fail-closed ANTES de qualquer I/O
     ├── IPartitionExecutionStore.FindCanonicalAsync           ─── réplay idempotente barato
     ├── IPstCustodyStore.FindAsync(scope, artifact)           ─── staleness: custódia atual vs. plano
     ├── IPartitionPartWriter.ExecuteAsync(...)                ─── I/O real, isolado atrás da porta
     └── IPartitionExecutionStore.SaveAsync                    ─── checkpoint 3, append-only

Infrastructure.PstProcessing (adapter substituível)
  LocalSinglePartExecutionWriter  ── único IPartitionPartWriter deste Passo; stage→verify→publish→reverify
  SqlPartitionExecutionStore      ── SQL Server, RLS + filtro project_id, INSERT único (toda linha é canônica)
  TempStagingReconciler           ── manutenção explícita, nunca automática

Domain.PstProcessing
  PartitionExecutionId · PartitionExecutorIdentity · PartitionExecutionRecord (Complete/Rehydrate)
```

- **Substituibilidade**: `IPartitionPartWriter` é a fronteira, no mesmo espírito de `IPartitionPlanner`
  (Passo 2) e `IPstEngine` (Passo 1). Quando um split real multi-part for autorizado por ADR, um writer
  semântico implementa a MESMA porta; nada em Domain/Application muda.
- Domain/Contracts/Application permanecem independentes de parser/fornecedor e de ASP.NET
  (`DependencyRuleTests`).

## Modelo de dados (migration `0022_slice4b_partition_execution.sql`, aditiva)

- **`dbo.pst_partition_executions`** — diferente de `pst_partition_plans`/`pst_inspections`, esta tabela
  NUNCA guarda tentativas fracassadas: uma linha só existe depois que o writer confirmou o output por
  reabertura/reinspeção. Por isso não há coluna `is_canonical`/`outcome` — todo INSERT já é canônico por
  construção, e um índice único simples (`UX_pst_partition_executions_canonical`, não filtrado) sobre
  `(tenant_id, project_id, plan_id, part_id)` é o backstop completo de idempotência/concorrência.
- Guarda apenas: IDs opacos (execução, plano, parte, artefato), `plan_hash`/`part_key`/`part_sequence`
  (identidade), `source_hash`/`source_size_bytes`, `output_hash`/`output_size_bytes`, nome/versão do
  executor, correlação e timestamps. **Nenhum caminho físico é persistido** — o local do output é sempre
  derivado, em tempo de leitura, dos IDs opacos já persistidos.
- FK composta ao plano e à parte amarra a execução ao mesmo tenant/projeto/plano no banco (a constraint
  `UQ_pst_partition_plan_parts_scope`, adicionada de forma aditiva por esta migration, é o que torna esse FK
  possível — mesmo padrão do Passo 2 com `UQ_pst_inspections_scope`).
- `CK_pst_partition_executions_byte_identical` trava, no banco, o mesmo invariante reforçado no Domain
  (output byte-for-byte idêntico à origem).
- `GRANT SELECT, INSERT` apenas a `ab_app_role` (append-only; manutenção não recebe grant algum); a tabela
  participa de `rls.tenant_isolation_policy` (FILTER + BLOCK AFTER INSERT).
- Migrations 0001–0021 permanecem **byte-for-byte** intactas (`MigrationHashTests`).

## Output no filesystem (bundle imutável)

```text
<OutputRoot>/<tenantId:N>/<projectId:N>/<planId:N>/<partKey>/
  part.pst        — cópia byte-for-byte da origem
  part.sha256     — sidecar com o hash hex (mesmo padrão de FileSystemMappingArtifactStore)
  manifest.json   — {tenant, project, plan, part, planHash, partSequence, partKey,
                      outputHash, outputSizeBytes, executorName, executorVersion}
```

Nenhum campo do manifesto (SQL ou `manifest.json`) carrega caminho absoluto, UPN/mailbox completo,
assunto/corpo, nome de anexo, SAS/segredo ou bytes de conteúdo — provado lendo TODAS as colunas persistidas
e o `manifest.json` inteiro (`ThePersistedExecutionAndManifestCarryNoPathFileNameOrMailboxIdentifier`). A
raiz de output é sempre distinta da raiz de custódia de origem (validado no startup por
`PstPartitionExecutionOptions.ValidateForOperation`) — o writer nunca escreve dentro da árvore read-only de
origem.

## Segurança e resiliência (delta sobre o Passo 2)

- **Anti-IDOR**: `IPartitionPlanStore.FindByIdAsync` filtra por escopo; cross-tenant/cross-project lançam
  `PartitionPlanNotFoundException` indistinguível de "não existe"
  (`CrossTenantAndCrossProjectExecutionIsDeniedIndistinguishablyFromNotFound`).
- **Containment/anti-symlink**: `ArtifactPathContainment.EnsureContained` protege tanto a resolução da
  origem (dupla checagem, mesmo padrão de `HeaderOnlyPstInspectionEngine`) quanto todo caminho de output
  (staging root, staging dir, diretório final).
- **Preflight de espaço**: `DriveInfo.AvailableFreeSpace` verificado ANTES de abrir qualquer stream de
  escrita; insuficiência nunca produz sucesso parcial (`InsufficientDiskSpacePreflightFailsClosedWithoutWritingAnything`).
- **Timeout/cancelamento**: um `CancellationTokenSource` vinculado com `CancelAfter(Timeout)` distingue
  timeout interno (convertido em `PartitionExecutionLimitExceededException("TIMEOUT")`) de cancelamento do
  chamador (propagado como `OperationCanceledException`) — nos dois casos, nenhum output canônico é publicado
  (`ACopyThatNeverCompletesIsAbortedByTheConfiguredTimeoutWithoutPublishingAnyOutput`,
  `CallerCancellationDuringTheCopyNeverPublishesAnyOutput`).
- **SQL injection**: todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`).

## Operação (runbook do Passo 3)

- Capacidade **desabilitada por padrão**: `PstPartitionExecution:Enabled=false`. Habilitar exige
  `PstPartitionPlanning:Enabled=true` (só executa um plano já persistido) — habilitar sem planejamento
  derruba o host no startup em vez de ser silenciosamente ignorado.
- `PstPartitionExecution:OutputRootPath` é obrigatório e deve ser DISTINTO de `PstInspection:RootPath` —
  validado no startup, nunca detectado só na primeira execução real.
- `MinFreeSpaceMarginBytes`/`TimeoutSeconds` são configuráveis por implantação; valores não positivos
  derrubam o startup.
- `TempStagingReconciler.CleanupOrphans` é uma operação de manutenção explícita — nenhum worker novo é
  registrado neste Passo (ver STOP-THE-LINE). Um operador (ou job de manutenção de slice futuro) decide
  quando/com que limiar de idade invocá-la.
- Nenhum worker/fila é registrado: `ExecutePartitionPlanUseCase` é diretamente invocável (testes e
  composição futura), como nos Passos 1 e 2.

## Critérios de aceite (mapeamento para AB-4B-006)

| # | Critério | Onde é provado |
| --- | --- | --- |
| 1 | Output imutável byte-for-byte, hash/tamanho fecham | `SinglePartWithinTargetProducesAByteForByteVerifiedOutputAndLeavesTheSourceUntouched`, `Slice4bPartitionExecutionDomainTests.CompleteAcceptsAnOutputThatIsByteForByteIdenticalToTheSource` |
| 2 | Source permanece byte-for-byte inalterado | `SinglePartWithinTargetProducesAByteForByteVerifiedOutputAndLeavesTheSourceUntouched` |
| 3 | Output só canônico após write+flush/close+hash+reabertura+manifesto/checkpoint | protocolo de 3 checkpoints acima; `ACrashAfterFinalizationButBeforeThePersistCheckpointConvergesWithoutDuplicating` |
| 4 | Unsupported/Blocked/não-canônico/forjado/cross-scope/stale ⇒ zero efeito | `AnUnsupportedPlanIsRejectedWithZeroOutputAndZeroExecutionRows`, `ABlockedPlanIsRejectedWithZeroOutputAndZeroExecutionRows`, `ForgedPlanIdentityIsRejectedBeforeAnyIO`, `CrossTenantAndCrossProjectExecutionIsDeniedIndistinguishablyFromNotFound`, `SourceThatDriftedOnDiskAfterPlanningIsRejectedBeforeAnyCanonicalOutputIsPublished` |
| 5 | Réplay idempotente do mesmo plano | `IdempotentReplayReturnsTheSameCanonicalExecutionWithoutRewritingTheOutput` |
| 6 | Concorrência converge para um único resultado | `ConcurrentExecutionOfTheSamePlanConvergesToExactlyOneCanonicalResult` |
| 7 | Crash-safety em torno dos 3 checkpoints | `ACrashAfterFinalizationButBeforeThePersistCheckpointConvergesWithoutDuplicating`, `TamperingTheExistingOutputIsDetectedOnReadbackAndNeverSilentlyOverwritten` |
| 8 | Tampering detectado no replay/readback, nunca sobrescrito | `TamperingTheExistingOutputIsDetectedOnReadbackAndNeverSilentlyOverwritten` |
| 9 | Temp/staging órfão nunca confundido com sucesso; reconciliável | `AnOrphanStagingDirectoryIsNeverConfusedWithACompletedPartAndIsSafelyReconciled` |
| 10 | Manifesto sem PII/caminho/segredo | `ThePersistedExecutionAndManifestCarryNoPathFileNameOrMailboxIdentifier` |
| 11 | Domain/Application independentes de parser/vendor/ASP.NET | `DependencyRuleTests` (inalterados, verdes) |
| 12 | Migrations anteriores intactas; 0022 aditiva; least-privilege | `MigrationHashTests.Migration0022AppliesCleanlyAndPriorHashesRemainStable` |
| 13 | CI completo verde no HEAD final | gates do `.github/workflows/ci.yml` |

## Fora do escopo — STOP-THE-LINE

Nenhum código deste Passo implementa ou invoca: parser/writer/splitter Aspose, libpff ou qualquer vendor
novo; split multi-part real de PST acima do target; semantic partitioning por item/pasta/data/bin-packing;
repair/ScanPST/quarantine workflow além de registrar falha necessária; Export-EVArchive real; AzCopy/Azure
staging/SAS; Purview, Graph, Exchange Online ou import job; CSV builder do Purview; reconciliação final M365;
regeneração automática de part já usada em import; alteração de decisões ADR existentes; avanço para o
próximo Passo.

## Limitações residuais (para Passos futuros)

- Só `SinglePartWithinTarget` executa. Split real multi-part continua bloqueado pela mesma limitação de
  inventário do Passo 2 (`Unsupported / ItemInventoryUnavailable` nunca chega a este Passo).
- `TempStagingReconciler` é uma função pura invocável, não um worker agendado — a operação/orquestração de
  quando rodá-la é decisão de slice futuro.
- Nenhuma orquestração assíncrona (fila/worker) e nenhum endpoint HTTP/Portal foram adicionados — fora do
  escopo obrigatório deste Passo.
- A verificação de containment/reparse point permanece baseada em caminho (não em handle/descritor aberto),
  mesma limitação residual documentada no Passo 1 (AB-4B-002 item 3).

## Regra de encerramento

Este PR permanece **Draft** durante toda a implementação. Não marcar Ready nem fazer merge sem
`ARCHIVEBRIDGE_MERGE_APPROVED` para o HEAD corrente do Engineering Reviewer, com CI totalmente verde.
