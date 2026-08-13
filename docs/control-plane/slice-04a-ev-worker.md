# Slice 4A — Worker EV operacional (sub-incrementos 2 e 2B)

Consumidor durável e seguro dos comandos de descoberta Enterprise Vault. Fecha o
laço **SQL durable queue → pending scope → worker → processor → discovery →
evidência + SQL → Job terminal/retry** sem nenhuma superfície de disparo no
Portal (o Portal continua somente leitura; o botão/POST vem depois).

O sub-incremento **2B** adiciona a **recuperação de crash** que faltava: um laço
operacional que devolve à fila os Jobs cujo lease expirou depois de uma queda do
worker (Jobs órfãos presos em `Processing`), **escopado ao workload EnterpriseVault**.

## Fluxo de composição real

```
appsettings (EnterpriseVaultDiscovery:Enabled=true)
        │  fail-closed no startup: config inválida OU host não-Windows ⇒ o host não sobe
        ▼
EnterpriseVaultDiscoveryComposition.Configure(HostApplicationBuilder)
        │
        ├─ SqlEvDiscoveryPendingScopeReader ──(identidade de MANUTENÇÃO, READ-ONLY)
        │        enumera tenant/projeto com Job EV elegível — nada além do escopo
        │
        ├─ SqlJobLeaseManager  (Slice 1, Infrastructure/Jobs) — instância ÚNICA
        │        compartilhada pelo processor (RenewAsync) e pelo reaper (RecoverExpiredLeasesAsync)
        │
        └─ EvDiscoveryCommandProcessor  ◄── grafo REAL reutilizado:
                 SystemClock, TenantConnectionFactory,
                 SqlEvDiscoveryCommandInbox, SqlJobStore, SqlJobLeaseManager,
                 SqlProjectStore, SqlEvDiscoveryStore,
                 FileSystemEvDiscoveryEvidenceStore, EvDiscoveryEvidenceSerializer,
                 AdapterCompatibilityEvaluator(EvExportDocumented151Adapter),
                 PowerShellEvCapabilityDiscovery → WindowsEvPowerShellHost,
                 DiscoverEvCapabilitiesUseCase, EvDiscoveryPolicy.Default
        ▼
EvDiscoveryWorker  (BackgroundService, caminho normal)
  loop: scopes = reader.ListEligibleScopesAsync(MaxScopesPerPoll)
        foreach scope: processor.ProcessNextAsync(scope, workerId, lease, correlation)
        delay(PollInterval)

EvLeaseRecoveryWorker  (BackgroundService, caminho de RECUPERAÇÃO — 2B)
  loop: recovered = leaseManager.RecoverExpiredLeasesAsync(Workload.EnterpriseVault, LeaseRecoveryBatchSize)
        delay(LeaseRecoveryInterval)
```

Os dois workers são serviços hospedados **separados**: o de descoberta avança o
trabalho normal; o de recuperação limpa leases órfãos. Ambos partilham a **mesma**
instância de `SqlJobLeaseManager` (registrada como singleton na composição), de
modo que renovação e recuperação falam com o mesmo componente de lease/fencing.

`SyntheticEvJobWorker` é uma ferramenta de diagnóstico ISOLADA (classe própria):
`SyntheticJobMode:Enabled=false` por padrão e bloqueada em `Production`. Não
interfere no worker operacional.

## Recuperação de leases expirados (crash recovery — 2B)

Quando um worker EV cai no meio do processamento, o Job fica em `Processing` com
`owner_worker`/`lease_expires_at_utc` preenchidos e ninguém para avançá-lo. O
`EvLeaseRecoveryWorker` chama periodicamente
`IJobLeaseManager.RecoverExpiredLeasesAsync(Workload.EnterpriseVault, batchSize)`,
que sob a identidade de manutenção:

1. **Seleciona** (`SelectExpiredByWorkloadSql`) até `batchSize` Jobs em
   `state = Processing (1)` **do workload EnterpriseVault** com
   `lease_expires_at_utc < @now`, ordenados pelo lease mais antigo.
2. Para cada candidato, aplica um `UPDATE` transacional (`RecoverOneSql`) que
   **revalida** `state = 1 AND lease_epoch = @epoch AND lease_expires_at_utc < @now`
   e `(@workload IS NULL OR workload = @workload)`. Se a política de retry ainda
   tem tentativas, o Job vai para `RetryScheduled (2)` com `next_attempt_at_utc`
   agendado e razão `LeaseExpiredRecovered (6)`; se as tentativas se esgotaram, vai
   para `Failed (4)` com `last_error_code = ResourceExhaustion` e razão
   `AttemptsExhausted (7)`. Em ambos os casos `owner_worker` e
   `lease_expires_at_utc` são limpos e uma transição é registrada em
   `job_state_transitions`.

Após a recuperação, um novo claim reivindica o Job com **época maior**; o worker
antigo (época obsoleta) é **cercado** (`FencedOut`) em qualquer `Renew/Complete/Fail`.

### Proteção contra a corrida com o heartbeat

A revalidação de `lease_expires_at_utc < @now` **dentro** do `UPDATE` garante que,
se um heartbeat renovou o lease entre o `SELECT` e o `UPDATE`, o `UPDATE` não casa
nenhuma linha (`@@ROWCOUNT = 0`) e o Job **não é recuperado** — nenhum Job vivo é
arrancado do worker que ainda o detém. O teste
`ScopedReaperDoesNotRecoverAnUnexpiredLease` prova esse caminho: sem avançar o
relógio, o reaper escopado não toca o Job ainda válido.

## Isolamento por workload (STOP-THE-LINE — 2B)

O reaper EV recupera **apenas** o workload `EnterpriseVault`. A seleção
(`SelectExpiredByWorkloadSql`) filtra `workload = @workload` e o `UPDATE`
carrega `AND (@workload IS NULL OR workload = @workload)` como **defesa em
profundidade** — mesmo que um candidato de outro workload chegasse à fase de
`UPDATE`, ele não seria alterado. Jobs de outros workloads (por exemplo, `Pst`)
com lease expirado permanecem intocados pelo Worker EV: quem os recupera é o
reaper daquele workload (ou o caminho global `RecoverExpiredLeasesAsync(batchSize)`
sem workload, cujo comportamento aceito foi preservado). O teste
`EvLeaseRecoveryDoesNotRecoverOtherWorkloads` prova o isolamento com um Job `Pst`
expirado que continua `Processing` com owner preservado após o reaper EV rodar.

## Fronteira de segurança da identidade de manutenção (STOP-THE-LINE)

A identidade de manutenção (`OpenForMaintenanceAsync`, sem RLS por tenant) é
restrita a **operações técnicas cross-tenant aprovadas** — e são exatamente **duas**:

1. **Enumeração de escopos** (`SqlEvDiscoveryPendingScopeReader`): responde "há
   trabalho EV elegível neste tenant/projeto?" com um `SELECT DISTINCT`
   estritamente READ-ONLY que **não** retorna site, directory server, solicitante,
   hashes, evidência nem conteúdo.
2. **Recuperação de leases expirados** (`SqlJobLeaseManager`, Slice 1,
   `Infrastructure/Jobs`): devolve à fila Jobs órfãos cujo lease venceu, operando
   apenas sobre colunas técnicas de lease/estado (`state`, `lease_epoch`,
   `owner_worker`, `lease_expires_at_utc`, `next_attempt_at_utc`) — nunca sobre
   dados de negócio EV.

Nenhuma dessas operações é **autorização** nem produz **efeito de negócio**. A
partir do `TenantScope` descoberto, **todo** o processamento (claim, execução,
persistência, conclusão, retry, publicação) volta à identidade normal da aplicação
(`OpenForTenantAsync` → RLS por tenant + filtro por `project_id`). É proibido usar
manutenção para claim/alteração/attempt/processamento/persistência/publicação/
finalização/discovery/evidência.

> **Correção explícita:** ao contrário do que descrevia o sub-incremento 2, a
> identidade de manutenção **não** é usada *exclusivamente* pelo
> `SqlEvDiscoveryPendingScopeReader`. A recuperação de leases expirados é a **outra**
> operação cross-tenant aprovada. A distinção arquitetural que se mantém é: dentro do
> **código EV específico** (`Infrastructure/EnterpriseVault`), a única identidade de
> manutenção continua sendo a enumeração de escopos; a recuperação de leases reutiliza
> o componente genérico de Slice 1 (`Infrastructure/Jobs`), fora do diretório EV.

Um teste arquitetural
(`MaintenanceIdentityIsRestrictedToApprovedCrossTenantInfrastructureOperations`)
garante que, em `Infrastructure/EnterpriseVault`, `OpenForMaintenanceAsync` só
aparece no leitor de escopos; e testes de integração provam que, após a enumeração,
o processamento de um escopo não alcança o comando de outro, e que a recuperação de
lease respeita o isolamento por workload e a corrida com o heartbeat.

## Configuração (fail-closed)

```yaml
EnterpriseVaultDiscovery:
  Enabled: false                  # padrão: nenhum worker EV executa
  PollIntervalSeconds: 5          # > 0
  LeaseSeconds: 30                # > 0
  MaxScopesPerPoll: 32            # 1..512
  LeaseRecoveryIntervalSeconds: 15  # > 0  (cadência do reaper de crash)
  LeaseRecoveryBatchSize: 64        # 1..1000
  EvidenceRoot: ""                # obrigatório quando Enabled=true
ConnectionStrings:
  Application: ""                 # obrigatória quando Enabled=true
  Maintenance: ""                 # obrigatória quando Enabled=true
```

- `Enabled=false` ⇒ **nenhum** worker EV é registrado (nem descoberta, nem reaper).
- `Enabled=true` com `Application`/`Maintenance`/`EvidenceRoot` vazio, ou
  `PollInterval`/`Lease`/`MaxScopesPerPoll`/`LeaseRecoveryInterval`/
  `LeaseRecoveryBatchSize` inválidos ⇒ falha de startup.
- `Enabled=true` fora de um Windows Worker ⇒ falha explícita (`PlatformNotSupportedException`);
  nunca "Ready" com sonda falsa em produção. Nenhuma senha/credencial é versionada.

## Limites

`Export-EVArchive` não é executado (apenas seus metadados são observados);
`LaboratoryValidated` permanece `false`; nenhuma integração de Slice 4B
(AzCopy, Purview, Microsoft Graph, Exchange Online, PST, libpff) é introduzida.
PowerShell nunca executa no processo web. O worker de descoberta chama somente
`EvDiscoveryCommandProcessor.ProcessNextAsync` — sem execução inline. O worker de
recuperação chama somente `IJobLeaseManager.RecoverExpiredLeasesAsync(Workload, …)`
— sem claim nem efeito de negócio. Nenhum log sensível (tenant/projeto/site/
directory/comando/evidência/credencial) é emitido em qualquer dos caminhos.
