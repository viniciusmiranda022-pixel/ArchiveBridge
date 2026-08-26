<!-- Evidência executável do work order AB-I7-001 (I7 — Hardening — Passo 1). Fonte de autoridade: docs/runbook/06-parte-vi-plano-desenvolvimento.md §45.4 (chaos cases 164-175) e docs/engineering/requests/AB-I7-001.md. Este documento NÃO declara I7 concluído, NÃO declara Production Ready e NÃO introduz estados novos além dos que já existem no código (ver docs/engineering/recovery-runbook-i7.md sobre por que RECOVERY_REQUIRED/RECONCILING do ADR-0003 ainda não são usados). -->

# Matriz de failure mode — I7 Passo 1 (AB-I7-001)

**Estado:** evidência de hardening deste Passo. **Não** é uma declaração de Production Ready, canário ou
pen-test institucional (ver STOP-THE-LINE do work order). Cada linha liga um cenário de chaos/recovery
documentado no runbook a: o estado esperado do sistema, a ação automática que o código realmente executa
hoje, a ação humana necessária (se houver) e o(s) teste(s) automatizado(s) que provam o comportamento.

Convenção de mapeamento: sempre que o cenário do runbook usa uma palavra genérica ("provider", "rede",
"log sink"), a linha correspondente identifica o mecanismo REAL da arquitetura atual (on-premises, sem
Azure PaaS/Service Bus obrigatório) que o cenário exercita — nunca um mecanismo hipotético/futuro.

| # | Chaos case (runbook §45.4) | Mecanismo real exercitado | Estado esperado | Ação automática | Ação humana | Evidência (testes) |
| --- | --- | --- | --- | --- | --- | --- |
| 164 | Matar worker durante escrita de part | `LocalSinglePartExecutionWriter` — cópia PST em duas fases (staging → `Directory.Move` atômico) | Nenhum output no caminho final; staging órfão inofensivo (nunca confundido com sucesso) | Reconciliação por idade (`TempStagingReconciler`, fora do hot path); reexecução do zero produz o mesmo output canônico | Nenhuma (auto-recuperável); operador só age se o órfão persistir além do limiar de idade | `Slice4bPartitionExecutionTests.AnOrphanStagingDirectoryIsNeverConfusedWithACompletedPartAndIsSafelyReconciled`; `LocalSinglePartExecutionWriterLimitEnforcementTests` (timeout/cancelamento mid-copy); **novo:** `LocalSinglePartExecutionWriterMidCopyChaosTests.AScratchRootThatIsUnavailableForWritingFailsClosedWithoutPublishingAnyOutput` |
| 165 | Reiniciar após hash e antes do evento de custódia | Checkpoint 2 (bundle publicado no disco) vs. Checkpoint 3 (manifesto SQL via `IPartitionExecutionStore`) | Bundle físico válido, checkpoint SQL ausente | Reexecução converge: writer detecta o bundle já publicado e o valida antes de reaproveitar; a Application então persiste o checkpoint 3 uma única vez | Nenhuma | `Slice4bPartitionExecutionTests.ACrashAfterFinalizationButBeforeThePersistCheckpointConvergesWithoutDuplicating`; `Slice2PublishOutsideTxTests.CrashAfterReserveBeforePublishRecoversSameVersion` / `CrashAfterPublishBeforeFinalizeRecovers`; `Slice3EvDiscoveryTests` (par equivalente para evidência de descoberta EV) |
| 166 | Lease expira com worker ainda vivo | Fencing por `LeaseEpoch` (`SqlJobFence.GuardSql`, `UPDLOCK,HOLDLOCK`) + `PlanningHeartbeat` | Owner antigo cercado (`FencedOutException`) em qualquer efeito subsequente; job liberado para reclaim | Reaper (`RecoverExpiredLeasesAsync`) recupera para `RetryScheduled` (se restam tentativas) ou `Failed` (`AttemptsExhausted`) | Nenhuma no caminho feliz; operador revisa jobs `Failed` por `AttemptsExhausted` | `FencingAndRecoveryTests`; `HeartbeatReaperRaceTests`; `Slice2FencingHardeningTests` (heartbeat perdido bloqueia o efeito mesmo antes do reaper rodar); `Slice4aEvLeaseRecoveryTests` |
| 167 | SQL indisponível após efeito externo confirmado/possível, antes do state update | `PlanningHeartbeat.RunWhileAsync` — qualquer exceção (incl. de conectividade) na renovação do lease cancela a operação ANTES de persistir novo efeito | Operação corrente cancelada (`Fenced`); nenhum efeito novo persistido; nenhuma conclusão indevida | Job permanece com lease ativo até expirar; reaper decide retry/Failed normalmente | Operador revisa jobs presos em `Processing` além do esperado (sinal de perda de conectividade prolongada) | `EvExportThrottleLeaseLossTests.LosingTheThrottleLeaseDuringExecutionFencesTheOperationEvenWithAHealthyJobLease` (renovação falha ⇒ Fenced, zero efeito, zero `Complete`) — mesmo mecanismo (`PlanningHeartbeat`) usado por `PurviewUploadCommandProcessor` |
| 168 | Entrega/reexecução duplicada de comando/job (o runbook cita "Service Bus"; a baseline on-premises não usa Service Bus — o mecanismo real é o claim/fencing sobre `dbo.jobs`) | Idempotência por réplay (`JobCommandOutcome.IdempotentReplay`) + dedup natural-key nos artefatos | Segunda entrega do mesmo comando converge para o MESMO resultado, nunca duplica efeito/linha | Réplay detectado e absorvido sem reexecutar o efeito externo | Nenhuma | `IdempotencyAndRetryTests`; `WorkerReplayAndOwnerTests`; `ReconciliationCertificateIntegrationTests.IssueConvergesUnderFiveConcurrentIdenticalIssuancesInsteadOfDuplicating`; `Slice4cEvExportTests.DuplicateConcurrentRequestsWithTheSameCanonicalIdentityConvergeToOneLogicalRequest`; **novo:** `PurviewUploadUseCaseTests.ASasConsumedByAFailedAttemptIsNeverReacquiredByARetryAndTheJobNeverFalselyCompletes` (réplay do Job após falha nunca reexecuta o transporte com o mesmo SAS) |
| 169 | SAS expira durante o upload | `PurviewSasUploadHandle` — SAS de uso único (`Consumed` é terminal, nunca retorna a `Available`) | Tentativa marcada `ProcessFailed`/`SasDenied` (nunca `Uploaded`); handle nunca reaproveitado | Job agendado para retry (`JobRetryGate.ScheduleRetryOrFailAsync`) ENQUANTO houver orçamento (`RetryPolicy.ShouldRetry`); a NOVA tentativa é negada fail-closed se tentar reusar o mesmo handle consumido; ao esgotar o orçamento, a MESMA chamada converge atomicamente a `Failed` (`ErrorCode.ResourceExhaustion`) — nunca reentra em `RetryScheduled` (AB-I7-002, ver nota abaixo) | Nenhuma no caminho de retry; operador revisa jobs `Failed` por `ResourceExhaustion` (mesmo sinal já usado pelo reaper de lease expirado) | `PurviewUploadUseCaseTests.ASasConsumedByAFailedAttemptIsNeverReacquiredByARetryAndTheJobNeverFalselyCompletes`; `Slice5PurviewSasUseCaseTests.ASecondAcquireAttemptAfterConsumptionIsDenied`; **novo (AB-I7-002):** `PurviewUploadUseCaseTests.ASasThatCannotBeAcquiredOnceTheRetryBudgetIsExhaustedFailsTheJobInsteadOfRetryingForever`; `JobRetryGateTests`; `JobRetryGateSqlTests` |
| 170 | DNS falha; rede perde pacotes (a baseline on-premises não tem chamada HTTP direta a um provider externo neste Passo — AzCopy/EV export são processos externos, não clientes HTTP do próprio código; o único ponto de rede do próprio processo é a conexão SQL Server) | Falha transitória de renovação de lease/conexão tratada como perda de fencing (mesmo caminho do cenário 167) | Operação corrente cancelada, nenhum efeito novo | Idem cenário 167 | Idem cenário 167 | `EvExportThrottleLeaseLossTests` (mapeamento por analogia — ver nota de aplicabilidade abaixo) |
| 171 | Scratch fica sem espaço | Preflight de espaço (`LocalSinglePartExecutionWriter.EnsurePreflightSpace`, `DriveInfo.AvailableFreeSpace`) | `PartitionExecutionLimitExceededException("INSUFFICIENT_SPACE")`; nenhuma escrita | Fail-closed antes de qualquer I/O; nenhum staging criado | Operador provisiona espaço; reexecução funciona normalmente depois | `Slice4bPartitionExecutionTests.InsufficientDiskSpacePreflightFailsClosedWithoutWritingAnything` (preflight); **novo:** `LocalSinglePartExecutionWriterMidCopyChaosTests.AScratchRootThatIsUnavailableForWritingFailsClosedWithoutPublishingAnyOutput` (escrita bloqueada/incompleta no meio do processo — scratch quebrado, não só cheio) |
| 172 | Sink de log/telemetria indisponível | Ver nota de aplicabilidade abaixo — **não aplicável hoje** | — | — | — | — (nenhum teste fabricado; ver rationale) |
| 173 | Provider retorna 429/5xx/resultado ambíguo | `IAzCopyUploadExecutor.UploadFileAsync` / `IEvArchiveExportExecutor.RunAsync` — exit code + `TimedOut` + `OutputLimitExceeded` como sinal de resultado do processo externo | Qualquer combinação não estritamente "sucesso limpo" (incl. exit 0 + timeout, o caso ambíguo) é tratada como falha, nunca como sucesso | `ProcessFailed`/`Failed` ⇒ retry agendado ENQUANTO houver orçamento (mesmo `JobRetryGate` do cenário 169); esgotado, converge a `Failed` | Ver cenário 169 (mesmo mecanismo unificado de orçamento) | `PurviewUploadUseCaseTests.AProcessFailureIsRetriedAndNeverProducesUploaded`; **novo:** `PurviewUploadUseCaseTests.AProcessResultThatTimesOutDespiteAZeroExitCodeIsTreatedAsFailureNeverAsUploaded`; `Slice4cEvExportTests.RetryScheduledIsAlsoAuditedForATransientProcessFailure`; **novo:** `Slice4cEvExportTests.AProcessResultThatTimesOutDespiteAZeroExitCodeIsNeverTreatedAsCompleted` |
| 174 | Identidade/permissão é removida durante a operação | Papéis contidos do SQL Server (`ab_app_role`/`ab_maintenance_role`, `0002_security_roles.sql`) — a fronteira de autorização REAL da aplicação; e a autorização de `AcquireSasForUploadUseCase` por `WorkloadIdentity` | Operação em curso falha fechado (`SqlException` de permissão, ou `PurviewSasAcquisitionDeniedException`); nenhum efeito parcial persistido | Exceção propaga sem ser mascarada como sucesso/negócio; nenhuma transação commita | Operador restaura a permissão; a MESMA identidade volta a operar sem estado corrompido | `Slice5PurviewSasUseCaseTests.AcquireByAnUnauthorizedIdentityIsDeniedAndNeverTouchesTheSecretStore`; **novo:** `IdentityPermissionRevocationTests.RevokingTheApplicationRoleMidOperationFailsClosedWithoutPartialStateAndRecoversAfterRestoration` |
| 175 | Arquivo origem muda no meio (do processamento) | `LocalSinglePartExecutionWriter` — hash recomputado em streaming durante a cópia, nunca confiado de antemão | `PartitionExecutionSourceStaleException`; nenhum output canônico publicado | Fail-closed imediato (origem cresce) ou ao final da cópia (conteúdo diverge do hash do plano) | Operador investiga por que a origem sob custódia mudou; reexecução com a origem estável funciona normalmente | `Slice4bPartitionExecutionTests.SourceThatDriftedOnDiskAfterPlanningIsRejectedBeforeAnyCanonicalOutputIsPublished` (origem trocada ANTES da cópia começar); **novo:** `LocalSinglePartExecutionWriterMidCopyChaosTests.SourceThatGrowsWhileBeingCopiedIsAbortedImmediatelyWithoutPublishingAnyOutput` e `SourceThatIsRewrittenWithDifferentContentPartwayThroughTheReadIsRejectedWithoutPublishingAnyOutput` (origem muda DURANTE a leitura ativa — TOCTOU) |

## Notas de aplicabilidade (cenários 170 e 172)

O work order AB-I7-001 escopa explicitamente "os cenários documentados **aplicáveis à arquitetura
atual**". Dois cenários do runbook (§45.4) foram escritos para uma topologia genérica com provider HTTP
externo e sink de telemetria dedicado; a arquitetura on-premises vigente (ADR-0003, aceito) não tem hoje
nenhum dos dois como dependência funcional:

- **#172 "sink de log/telemetria indisponível":** os workers de host (`ReconciliationWorker`,
  `EvidenceWorker`) são **scaffolding não funcional** (ver seus próprios comentários de classe) — nenhum
  processamento real ainda passa por eles. O único "log" com semântica de durabilidade que a arquitetura já
  garante é a trilha de auditoria (`dbo.job_state_transitions`, `dbo.job_attempts`, os `*_events` de EV/
  Purview), que é escrita **na mesma transação SQL** do efeito de negócio — nunca um sink best-effort
  separado. Perder essa trilha é, por construção, o MESMO cenário que "SQL indisponível" (#167), já coberto.
  Não foi fabricado nenhum teste de "logger que lança exceção" porque isso não provaria nada sobre um
  mecanismo de produção real; quando um worker de host funcional for implementado num Passo futuro com um
  provider de log best-effort de verdade, este cenário deve ser revisitado.
- **#170 "DNS falha; rede perde pacotes":** neste Passo, a única dependência de rede do PRÓPRIO processo
  ArchiveBridge é a conexão com o SQL Server — AzCopy e o exporter EV são processos externos (linha de
  comando), não chamadas HTTP feitas pelo código gerenciado. Uma falha de rede/DNS observável pelo processo
  se manifesta, portanto, como uma exceção de conectividade SQL durante a renovação do lease — o MESMO
  mecanismo do cenário #167, já provado por `EvExportThrottleLeaseLossTests`. Quando um adapter HTTP direto
  a um provider (Graph/EXO, fora do escopo aceito hoje — ver ADR-0007) existir, este cenário deve ganhar
  cobertura própria com fault injection de transporte HTTP.

## Retry ativo sob orçamento — convergência terminal garantida (AB-I7-002)

**Corrigido nesta Passo** (AB-I7-002 — blocker levantado pelo Engineering Reviewer em comentário no PR
sobre o Passo 1, AB-I7-001; sem work order versionado separado). O Passo 1 havia
documentado aqui, como achado residual para o Engineering Reviewer, que `SqlJobStore.ScheduleRetryAsync`
— chamado diretamente por `PurviewUploadCommandProcessor`/`EvExportCommandProcessor`/
`EvDiscoveryCommandProcessor`/`PlanningCommandProcessor` diante de uma falha ATIVA (SAS negado, processo
do provider falhou, concorrência perdida) — nunca consultava `RetryPolicy.ShouldRetry`/`AttemptCount`; só
o reaper de lease expirado (`SqlJobLeaseManager`) aplicava esse limite. Uma causa de falha ativa que nunca
se resolvesse (ex.: SAS permanentemente consumido) podia fazer o job oscilar indefinidamente entre
`Processing`/`RetryScheduled` sem NUNCA convergir a `Failed`.

`ArchiveBridge.Application.Jobs.JobRetryGate` fecha essa lacuna sendo o ÚNICO caminho pelo qual os quatro
processadores acima agendam retry automático após falha ativa: consulta a MESMA `RetryPolicy`/contagem de
tentativas já persistida (a fonte de verdade em SQL, lida via `IJobStore.GetAsync` sob a MESMA transação
lógica da decisão) e só agenda nova tentativa (`Processing → RetryScheduled`) enquanto houver orçamento;
caso contrário converge atomicamente para `Failed` com `ErrorCode.ResourceExhaustion` — o MESMO código
estável já usado pelo reaper ao esgotar tentativas por expiração de lease — nunca reentrando em
`RetryScheduled`. A escrita permanece sob fencing (owner_worker + lease_epoch): um dono/época defasados
nunca agendam retry nem consomem orçamento, mesmo que a leitura consultiva do orçamento (antes da escrita
cercada) sugerisse que há orçamento disponível.

Invariantes preservados e provados por teste: nenhuma duplicação de efeito/evidência (a convergência a
`Failed` usa a MESMA transição atômica já auditada, `dbo.job_state_transitions`), nenhuma ressureição de
Job terminal por corrida com o reaper (o reaper só seleciona `state = 1`/Processing — `Failed` nunca é
elegível), fencing intacto sob owner/época defasados. Evidência: `JobRetryGateTests` (unitário, todas as
combinações de orçamento dentro/fora do limite, política customizável), `JobRetryGateSqlTests` (SQL Server
real: convergência exatamente uma vez a `Failed`, fencing sob época defasada não consome orçamento, corrida
com o reaper não ressuscita `Failed`), `PurviewUploadUseCaseTests.ASasThatCannotBeAcquiredOnceTheRetryBudgetIsExhaustedFailsTheJobInsteadOfRetryingForever`
(o cenário concreto do SAS permanentemente indisponível do cenário #169 acima).
