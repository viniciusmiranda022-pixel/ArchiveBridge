<!-- Evidência do work order AB-I7-001 item 10 (§1-6) e do work order AB-I7-005 item 11 (§7, I7 Passo 3). Documenta recovery para os estados/mecanismos REALMENTE existentes no código hoje — não os estados aspiracionais de ADR-0003 (RECOVERY_REQUIRED/RECONCILING/dead_letter_jobs/external_operations ledger), que permanecem projetados mas NÃO implementados (nenhuma tabela, nenhum caller). Ver nota de rastreabilidade ao final. -->

# Recovery runbook — estados reais (I7 Passo 1 + Passo 3)

**Autoridade:** este documento não substitui nem contradiz nenhum ADR; documenta o comportamento de
recuperação que o CÓDIGO hoje realmente implementa, para os operadores que precisam agir quando um destes
estados aparece. Quando um mecanismo aqui descrito divergir de um ADR aceito, o ADR continua sendo a fonte
de verdade sobre a intenção arquitetural; este runbook só descreve o que já existe.

## 1. Job preso em `Processing` além do lease esperado

**Como identificar:** consultar `dbo.jobs` por linhas com `state = Processing` e
`lease_expires_at_utc` no passado, ou pelo `IJobAuditReader`/`SqlJobAuditReader` por transições ausentes há
mais tempo que o esperado para o workload.

**Recuperação automática:** o reaper (`IJobLeaseManager.RecoverExpiredLeasesAsync`, escopável por
`Workload`) resolve isso sozinho na próxima execução agendada — não requer ação manual no caminho feliz:

- Se restam tentativas (`RetryPolicy.ShouldRetry(AttemptCount)`): `Processing → RetryScheduled`
  (`ReasonCode.LeaseExpiredRecovered`); o job volta a ser elegível para claim assim que `NextAttemptAtUtc`
  vencer.
- Se as tentativas se esgotaram: `Processing → Failed` (`ReasonCode.AttemptsExhausted`,
  `ErrorCode.ResourceExhaustion`).

**Ação humana:** nenhuma no caminho feliz. Revisar jobs `Failed` por `AttemptsExhausted` para decidir se o
trabalho precisa ser reenfileirado manualmente (`RequestManualRetryAsync`, elegível apenas a partir de
`RetryScheduled` — nunca ressuscita um job já `Failed`/`Completed`/`Cancelled`) ou se indica um problema
externo persistente (ex.: SAS permanentemente consumido — ver §3).

## 2. Job `Fenced` (owner perdeu a titularidade em pleno voo)

**Como identificar:** o processador de comando (`PurviewUploadCommandProcessor`/`EvExportCommandProcessor`)
retorna `Outcome = Fenced` para a chamada corrente — nenhuma linha nova em `dbo.job_state_transitions` para
esta tentativa (uma tentativa cercada nunca grava efeito).

**O que já aconteceu:** a perda de heartbeat (`PlanningHeartbeat.RunWhileAsync`) cancelou a operação ANTES
de qualquer persistência de efeito novo — nem `Complete`, nem `Fail`, nem `ScheduleRetry` foram chamados
por ESTE worker/época. O job permanece exatamente como estava (ainda `Processing` sob a época antiga, até
o lease expirar de verdade) ou já foi reivindicado por outro worker sob uma época nova.

**Recuperação automática:** nenhuma ação adicional é necessária — o job segue o fluxo normal do §1 quando o
lease expirar (se ninguém mais o reivindicou) ou já está sendo processado pelo novo titular.

**Ação humana:** nenhuma. `Fenced` é o desfecho CORRETO e esperado, não um erro a corrigir.

## 3. Tentativa negada por SAS/handle consumido (upload Purview)

**Como identificar:** `IPurviewUploadAttemptStore.ListAttemptsAsync` mostra múltiplas tentativas com
`Outcome = SasDenied` para o mesmo pedido, sem nenhuma `Uploaded` intercalada.

**O que já aconteceu:** uma tentativa anterior consumiu o SAS (uso único — `SasHandleState.Consumed` é
terminal) e depois falhou no transporte (ex.: o SAS expirou durante o próprio upload do AzCopy, ou o
processo falhou por qualquer outro motivo APÓS a leitura do segredo). O sistema nunca reutiliza o segredo
já lido nem tenta AzCopy de novo sem um SAS válido — cada nova tentativa é negada fail-closed
(`PurviewSasAcquisitionDeniedException` → `SasDenied`).

**Recuperação automática:** cada tentativa negada passa por `JobRetryGate.ScheduleRetryOrFailAsync`
(AB-I7-002 — ver [`chaos-failure-mode-matrix.md`](chaos-failure-mode-matrix.md)), que consulta o MESMO
orçamento (`RetryPolicy.ShouldRetry`/`AttemptCount`) já usado pelo reaper do §1. Sem uma nova geração de
SAS, o job retenta enquanto houver orçamento e então converge automaticamente para `Failed`
(`ErrorCode.ResourceExhaustion`) — nunca fica preso retentando indefinidamente.

**Ação humana:** revisar o job `Failed` por `ResourceExhaustion` (mesmo sinal do §1); emitir um NOVO SAS
para a wave (novo `Intake` — `PurviewSasUploadHandle.Intake` versiona e supera a geração anterior, mantendo
a anterior como `Destroyed`/histórico) e reabrir a solicitação de upload através do fluxo normal
(`RequestPurviewUploadUseCase`, idempotente por wave) — um Job já `Failed` nunca é reivindicado de novo
pelo mesmo pedido; a reabertura cria um novo ciclo.

## 4. Output tampered / manifesto divergente (PST partition, EV export, mapping)

**Como identificar:** `PartitionExecutionOutputTamperedException` (PST), `EvExportManifestValidationException`
(EV), ou os `*IntegrityViolationException` de reconciliação/certificado no log da aplicação, sempre
acompanhado do `reason code` sanitizado (nunca caminho físico/UPN/conteúdo).

**O que já aconteceu:** o bundle/manifesto no caminho canônico não confere com o que foi persistido/
esperado (bytes alterados, sidecar ausente, campo de lineage divergente). O sistema NUNCA sobrescreve
automaticamente — os bytes adulterados continuam exatamente como estavam.

**Recuperação automática:** nenhuma — é, por design, um bloqueio que exige investigação (poderia ser
corrupção de storage, tampering real, ou um bug). Nenhuma nova execução usa esse output como se fosse
válido.

**Ação humana:** investigar a causa raiz (storage subjacente, integridade do host) fora da aplicação;
remover manualmente o bundle adulterado do caminho canônico **somente** após confirmar que não é evidência
de um incidente de segurança que precise ser preservada; a reexecução do zero (mesmo plano/execução)
publica um novo bundle canônico válido.

## 5. Diretório de staging órfão

**Como identificar:** subdiretórios sob `<output-root>/.staging/` mais antigos que o limiar de
`TempStagingReconciler` sem um bundle canônico correspondente publicado.

**O que já aconteceu:** um crash real (sem exception handling algum) interrompeu uma escrita antes do
`Directory.Move` atômico para o caminho final — o staging nunca chegou a ser canônico e nunca será
confundido com um.

**Recuperação automática:** `TempStagingReconciler.CleanupOrphans` (rotina de manutenção, fora do hot path)
remove órfãos mais velhos que o limiar configurado. A reexecução normal do plano/execução produz o bundle
canônico independentemente do órfão.

**Ação humana:** nenhuma no caminho normal; se `TempStagingReconciler` não estiver agendado/rodando no
ambiente, um operador pode limpar manualmente (o staging nunca é lido como fonte de verdade por nenhum
caminho de código).

## 6. Permissão da identidade da aplicação revogada

**Como identificar:** exceções de SQL Server de permissão negada (`SqlException`) propagando para fora dos
stores/casos de uso, sem terem sido mascaradas como um resultado de negócio.

**O que já aconteceu:** a identidade contida (`ab_app`) perdeu a associação ao papel `ab_app_role` (ou
`ab_reaper` ao `ab_maintenance_role`) — nenhuma transação em curso commita; nenhum efeito parcial fica
visível.

**Recuperação automática:** nenhuma (correto — é uma decisão de segurança, não um erro transitório).

**Ação humana:** restaurar a associação de papel (`ALTER ROLE ab_app_role ADD MEMBER ab_app;`) via a
identidade administrativa; nenhuma limpeza adicional é necessária — a MESMA identidade volta a operar
normalmente assim que a permissão é restaurada (ver `IdentityPermissionRevocationTests`).

## 7. Recovery readiness / DR evidence (I7 Passo 3 — AB-I7-005)

**Autoridade:** `docs/runbook/05-parte-v-seguranca-infra-operacao.md` §40 (SLO/RTO/RPO) e §41 (backup/DR),
`docs/runbook/06-parte-vi-plano-desenvolvimento.md` I7/§45. Este Passo transforma esses requisitos
documentados em evidência EXECUTÁVEL — nunca uma declaração de configuração. Ver
[`dr-readiness-matrix.md`](dr-readiness-matrix.md) para o mapeamento completo critério de aceite → evidência
executável/teste.

### 7.1 Modelo de evidência

`ArchiveBridge.Domain.Recovery.RecoveryReadinessRecord` (tabela `dbo.recovery_readiness_evidence`,
migration `0040_i7_recovery_readiness_evidence.sql`) materializa cada exercício de recovery readiness como
um registro imutável, append-only, tenant/project-scoped e tamper-evident (mesmo padrão de self-hash
recomputado e revalidado fail-closed em toda leitura de
`ReconciliationCertificate`/`0038_i6_reconciliation_certificates.sql`). Quatro tipos de exercício
(`RecoveryExerciseType`): `RestoreDrill`, `PendingWorkRebuild`, `ArtifactEvidenceRecovery`, `HaFailover`.

O desfecho (`RecoveryReadinessStatus`) só tem três valores — `NotMeasured` (default fail-closed: nenhum
drill aplicável ainda executado), `Blocked` (limitação arquitetural comprovada OU objetivo não atingido por
um drill real) e `Pass` (o ÚNICO caminho que exige uma `RecoveryObjectiveMeasurement` real — início/fim
observados de uma execução de fato — e, quando há um alvo objetivo documentado, que a duração medida não o
exceda). Não existe NENHUM caminho de código que produza `Pass` a partir de configuração/alegação sem
execução — `RecoveryReadinessRecord.Pass` exige a medição como parâmetro obrigatório (não anulável) e
recusa (`RecoveryReadinessObjectiveNotMetException`) quando o objetivo não foi atingido.

**HA nunca é `Pass` nesta baseline:** `RecoveryReadinessRecord.Pass` lança
`RecoveryReadinessObjectiveNotMetException` incondicionalmente quando `ExerciseType == HaFailover` — bloqueio
estrutural no domínio, reforçado por um `CHECK` no próprio schema
(`CK_rre_ha_never_pass`, migration 0040) como defesa em profundidade caso qualquer código futuro tente
inserir a linha diretamente. Todo componente da baseline atual que dependa de proteção de segredo
single-node (DPAPI, sem KMS/HSM redundante ou failover comprovado) permanece `Blocked`, com o failure domain
documentado no próprio registro (`FailureDomain`) — nunca por documentação isolada que possa divergir do
código.

### 7.2 Restore drill

`ArchiveBridge.Integration.Tests.Support.RestoreDrillHarness` (SQL Server real) provisiona um banco de
teste efêmero PRÓPRIO e DEDICADO (nunca o banco compartilhado da suíte de integração, para não interferir
com os demais testes; nunca produção/cliente), aplica as migrations reais e executa `BACKUP DATABASE`/
`RESTORE DATABASE` nativos do SQL Server sobre esse banco, medindo a duração REAL de cada operação
(evidência de RTO). O drill prova, no mínimo (`RecoveryReadinessIntegrationTests`):

- estado canônico escrito ANTES do backup sobrevive ao restore com identidade/estado íntegros;
- estado escrito DEPOIS do backup é descartado pelo restore (prova de que o restore realmente reverteu o
  banco, não é um no-op);
- a duração medida (backup + restore) é registrada como `RecoveryObjectiveMeasurement` de
  `RecoveryObjective.ControlPlaneRto` contra o alvo documentado (`<= 4h`) — `Pass` só é possível se a
  medição real couber no alvo.

**Abort conditions:** falha ao provisionar o banco efêmero (SQL Server real indisponível/env var
`ARCHIVEBRIDGE_TEST_SQL` ausente) aborta o teste inteiro (fail-closed, sem fallback em memória); falha do
`RESTORE DATABASE` propaga sem mascarar como sucesso; o `finally` sempre tenta `SET MULTI_USER` mesmo se o
`RESTORE` falhar, para não deixar o banco efêmero preso em modo single-user (limpeza best-effort, sem
impacto em produção pois o banco é sempre descartável).

### 7.3 Pending-work rebuild

`ArchiveBridge.Contracts.Jobs.IPendingWorkRebuildQuery`/`SqlPendingWorkRebuildQuery` reconstrói o conjunto
de trabalho elegível EXCLUSIVAMENTE do estado persistido em `dbo.jobs`, reutilizando o MESMO predicado de
elegibilidade já usado pelo claim real (`SqlJobStore.ClaimSql`: `state IN (Pending, RetryScheduled) AND
(next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @asOf)`) em vez de duplicá-lo. É uma leitura pura —
nenhum lock de escrita, nenhuma mutação, nenhum efeito colateral; a reivindicação real permanece
exclusivamente `IJobStore.TryClaimNextAsync` (já atômico/fenced/idempotente), então a reconstrução NUNCA
duplica um efeito por si só (`PendingWorkRebuildIntegrationTests`):

- um Job `Pending`/`RetryScheduled` já devido aparece; um agendado para o futuro não aparece;
- um Job preso em `Processing` com lease expirado permanece INVISÍVEL à reconstrução até o reaper
  (`IJobLeaseManager.RecoverExpiredLeasesAsync`) convergê-lo para `RetryScheduled`/`Failed` — a
  reconstrução nunca ressuscita um lease diretamente;
- reexecutar a reconstrução é idempotente (mesma leitura, nenhuma mutação) e escopada por tenant/projeto
  (RLS + filtro explícito, mesmo padrão de `SqlJobStore`);
- duas reivindicações concorrentes do MESMO trabalho listado convergem para exatamente UM vencedor (a
  atomicidade já provada de `TryClaimNextAsync` — a reconstrução não introduz uma segunda fonte de
  concorrência).

### 7.4 Artifact/evidence recovery

Após um restore real, hashes/manifests/certificates continuam verificáveis porque cada tipo já é
tamper-evident por construção (`ReconciliationCertificate.Rehydrate`,
`RecoveryReadinessRecord.Rehydrate`): a leitura SEMPRE recomputa o hash a partir dos campos REALMENTE
carregados e recusa fail-closed qualquer divergência — nunca retorna um artifact/evidence adulterado como
válido. `RecoveryReadinessIntegrityViolationException` é o desfecho de uma adulteração pós-restore
detectada sobre o próprio registro de readiness (prova direta, `RecoveryReadinessIntegrationTests`); o
MESMO mecanismo (self-hash recomputado, `*IntegrityViolationException`) já protege certificates de
reconciliação e é reexercitado pelo restore drill acima (a integridade do estado canônico geral é a
pré-condição de qualquer artifact/evidence individual continuar válido).

### 7.5 RTO/RPO — objetivos documentados

| Objetivo | Alvo documentado | `RecoveryObjective` | Como é medido |
| --- | --- | --- | --- |
| Control Plane RTO | `<= 4h` | `ControlPlaneRto` | Duração real de backup+restore do drill (§7.2) |
| Control Plane RPO | `<= 5min` | `ControlPlaneRpo` | Duração real entre exercícios de rebuild/evidência consecutivos aplicáveis ao caminho testado |
| Evidence event (RPO lógico) | `0` (nenhuma perda lógica) | `EvidenceLogicalRpo` | Nenhuma perda de evidência tolerada — qualquer gap vira `Blocked`, nunca `Pass` |

Resultados não medidos permanecem `NotMeasured` por default (`RecoveryReadinessRecord.NotMeasured`) — não
existe nenhum caminho de código que promova `NotMeasured` a `Pass` sem uma execução real.

### 7.6 HA / failure domain (I7 Passo 3)

A baseline aceita permanece single-node/on-premises para o armazenamento de segredo da aplicação (DPAPI,
sem HSM/KMS redundante nem failover automático comprovado). Failure domain documentado:

- **O que é recuperável:** o estado de domínio em SQL Server (via backup/restore nativo, §7.2) e o
  trabalho pendente (via rebuild determinístico, §7.3) — nenhuma dependência de HA para isso.
- **O que exige intervenção operacional:** perda do host que detém o segredo protegido por DPAPI exige
  reprovisionamento manual da identidade/segredo — não há failover automático nesta baseline.
- **O que continua bloqueando Production Ready:** qualquer alegação de HA para esse componente. Este Passo
  NÃO introduz Azure Key Vault/HSM nem qualquer outro mecanismo de failover — permanece
  `RecoveryReadinessStatus.Blocked` explicitamente (STOP-THE-LINE do work order AB-I7-005).

---

## Nota de rastreabilidade: por que este runbook não usa `RECOVERY_REQUIRED`/`RECONCILING`

`docs/adr/0003-azure-sql-e-service-bus-premium.md` e `docs/engineering/secure-onprem-runbook.md` (§6.5-6.6,
§9) descrevem um desenho pretendido com estados `RECOVERY_REQUIRED`/`RECONCILING`, uma tabela
`dead_letter_jobs` e um `external_operations` ledger (`INTENT/SUBMITTED/CONFIRMED/AMBIGUOUS/FAILED`). Na
árvore de código atual:

- `JobState` (`src/ArchiveBridge.Domain/Jobs/JobState.cs`) só tem
  `Pending, Processing, RetryScheduled, Completed, Failed, Cancelled` — nenhum `RECOVERY_REQUIRED`/
  `RECONCILING`.
- `IExternalOperationLedger`/`ExternalOperation`/`ITargetIngestor`
  (`src/ArchiveBridge.Domain/TargetIngestion/ExternalOperation.cs`,
  `src/ArchiveBridge.Contracts/TargetIngestion/IExternalOperationLedger.cs`) existem como **scaffolding de
  design** — sem implementação SQL e sem nenhum call site.
- Não existe tabela `dead_letter_jobs` em `src/ArchiveBridge.Infrastructure/Migrations/`.

O work order AB-I7-001 (item 10) pede explicitamente um runbook "para os estados que realmente existem no
produto ... ou equivalentes vigentes, sem inventar novos estados quando os existentes bastarem" — por isso
este documento usa os mecanismos REAIS (`JobState`, `Fenced`, ledgers append-only de tentativa, exceções de
integridade tamper-evident) em vez dos estados do ADR ainda não implementados. Quando um incremento futuro
implementar de fato `RECOVERY_REQUIRED`/`RECONCILING`/o `external_operations` ledger, este runbook deve ser
revisado para refletir o mecanismo então vigente — a autoridade sobre a INTENÇÃO arquitetural continua
sendo o ADR-0003, aceito.
