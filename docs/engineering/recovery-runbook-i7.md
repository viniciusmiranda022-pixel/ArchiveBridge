<!-- Evidência do work order AB-I7-001 item 10. Documenta recovery para os estados/mecanismos REALMENTE existentes no código hoje — não os estados aspiracionais de ADR-0003 (RECOVERY_REQUIRED/RECONCILING/dead_letter_jobs/external_operations ledger), que permanecem projetados mas NÃO implementados (nenhuma tabela, nenhum caller). Ver nota de rastreabilidade ao final. -->

# Recovery runbook — estados reais (I7 Passo 1)

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
