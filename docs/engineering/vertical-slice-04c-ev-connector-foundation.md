# Vertical Slice 4C — Enterprise Vault Connector Foundation (Passo 1)

## Status

**Em desenvolvimento — PR deve permanecer em Draft até `ARCHIVEBRIDGE_MERGE_APPROVED` explícito do
Engineering Reviewer para o HEAD SHA corrente, com CI totalmente verde.**

Work order versionado: [`docs/engineering/requests/AB-4C-001.md`](requests/AB-4C-001.md) (`REQUEST_ID: AB-4C-001`).
Passo anterior (mergeado): [Partition Execution Foundation](vertical-slice-04b-partition-execution.md) (Slice 4B, Passo 3).

## Objetivo

Iniciar I4 / EPIC-05 (Enterprise Vault) estabelecendo a fundação segura do Source Connector: enrollment
outbound de uso único, identidade durável do connector, capability handshake contra a matriz de suporte, um
inventário read-only versionado e hashado, e os contratos de política de exportação — **sem** disparar
qualquer exportação real (`Export-EVArchive`), sem `Get-EVArchive` contra ambiente de cliente e sem nenhum
dos demais itens listados em [Fora do escopo](#fora-do-escopo--stop-the-line).

Este Passo estabelece a fronteira de confiança entre o Control Plane e o host Enterprise Vault: o connector
inicia SOMENTE conexões outbound (§15 do runbook), o Control Plane nunca recebe SMB nem credenciais EV, e
toda ação do connector fica atrás de contratos normalizados que não vazam tipos PowerShell/EV/vendor para
Domain ou Application.

## Modelo de identidade e enrollment (§15.1)

1. Um operador autenticado solicita um enrollment token (`IssueEnrollmentTokenUseCase`): a Application gera
   um segredo aleatório de 256 bits (`RandomNumberGenerator`), persiste APENAS o hash SHA-256 do segredo
   (`EnrollmentToken.TokenHash`) e devolve o segredo bruto uma única vez — ele nunca é recuperável depois.
   A janela de validade é sempre `EnrollmentToken.MaxTtl` (15 minutos); nenhum chamador pode pedir uma
   janela maior (`EnrollmentToken.Issue` recusa fail-closed).
2. O connector troca o segredo pela sua identidade durável (`RegisterConnectorUseCase`): apresenta o
   segredo bruto e o thumbprint da sua chave pública (`ConnectorPublicKeyThumbprint`, 64 hex minúsculos —
   material PÚBLICO; a chave privada nunca é conhecida pelo Control Plane). O resgate
   (`IEnrollmentTokenStore.RedeemAsync`) autentica-se SOMENTE pelo hash do segredo — nunca por um
   tenant/projeto informado pelo cliente; o tenant/projeto do connector resultante vêm inteiramente do
   token. Hash desconhecido, token expirado, já consumido (replay) ou revogado falham de forma
   INDISTINGUÍVEL externamente (`EnrollmentTokenNotFoundException` no limite de Contracts).
3. `ConnectorIdentity.Register`/`ReRegister` tornam o registro IDEMPOTENTE por (tenant, projeto,
   thumbprint): reinstalar o mesmo connector converge para a mesma identidade opaca (`ConnectorId`),
   atualizando apenas campos operacionais (hostname/site/versão); um connector já revogado nunca é
   reativado silenciosamente por uma reinstalação (`ConnectorRevokedException`).

## Capability handshake e matriz de suporte (AB-4C-001 critérios 4/5)

`SubmitConnectorCapabilityHandshakeUseCase` recebe a versão EV observada e a disponibilidade do snap-in
PowerShell, e delega ao Domain (`ConnectorCapabilityHandshake.Evaluate`) a única fonte de verdade sobre
`ExportCapable`: verdadeiro SOMENTE quando `ConnectorSupportMatrix.Evaluate(version)` retorna
`ConnectorSupportLevel.Certified` **e** o snap-in está disponível. A matriz embarcada
(`ConnectorSupportMatrix`) espelha [`docs/ev/compatibility-matrix.md`](../ev/compatibility-matrix.md) e
começa **sem nenhuma família certificada** — "compatível" nunca implica suporte comercial (regra de
honestidade de ADR-0013); qualquer schema/versão desconhecida cai em `Unknown` (fail-closed, nunca tratada
como suportada) e produz um diagnóstico estruturado (`BlockingReason`: `SCHEMA_VERSION_UNKNOWN`,
`FAMILY_NOT_SUPPORTED`, `POWERSHELL_SNAPIN_UNAVAILABLE` ou `FAMILY_NOT_CERTIFIED`), nunca um improviso. Cada
handshake é um registro append-only; o vigente é sempre o mais recente por `CollectedAtUtc`.

## Inventário read-only versionado (AB-4C-001 critérios 6/7/8)

`SubmitInventorySnapshotUseCase` sonda o inventário via `IEvInventoryAdapter` — porta substituível que roda
perto do dado; este Passo não exige uma implementação real de `Get-EVArchive` (ver
[Limitações residuais](#limitações-residuais-para-passos-futuros)) — e normaliza o resultado em
`InventorySnapshot`. Cada `InventoryArchiveRecord` carrega apenas identidade externa opaca do archive, tipo,
vault store (quando permitido) e status: **nunca** conteúdo de item, assunto/corpo/anexo ou credencial.

O snapshot é determinístico e hashado (`InventorySnapshot.ComputeHash`): os registros são ordenados
canonicamente por `ExternalArchiveId` antes do hash, e IDs duplicados na mesma coleta são recusados ANTES
de qualquer hash ser computado (`DuplicateInventoryArchiveException`). A Application decide, ANTES de
qualquer escrita, se o resultado é idêntico ao último snapshot persistido — réplay idempotente, nenhuma
linha nova (`InventorySnapshotAppendResult.Created == false`) — ou se representa mudança real, gerando uma
nova versão sem jamais reescrever a evidência anterior.

**Leitura/reidratação como fronteira NÃO CONFIÁVEL (AB-4C-003)**: `InventorySnapshot.Rehydrate` (mesmo
padrão de `PartitionPlan.Rehydrate`, Slice 4B) reaplica o MESMO caminho de canonicalização/deduplicação de
`Create` sobre os archives filhos REALMENTE carregados, valida `archive_count` do header contra a
quantidade real de filhos (antes um campo persistido e nunca conferido) e recomputa `ComputeHash`
comparando-o com o `snapshot_hash` gravado. Uma linha adulterada ou corrompida — filho alterado/removido,
`snapshot_hash` forjado, `archive_count` divergente — nunca é devolvida como snapshot canônico:
`SqlConnectorInventoryStore.GetLatestAsync` lança `InventorySnapshotIntegrityViolationException`, e o réplay
idempotente do caso de uso (que releé exatamente esse latest) falha fechado pelo mesmo motivo, em vez de
classificar incorretamente a corrupção como réplay idêntico ou perder a evidência em silêncio.

## Política de exportação — validada, nunca executada (AB-4C-001 itens 9-11)

`ExportRequestPolicy` valida os limites documentados do cmdlet `Export-EVArchive` (§16.3): `MaxPstSizeMb`
em `[500, 51200]` (default `18432`, a política do produto — margem abaixo dos 20 GB recomendados pela
Microsoft, ADR-0013) e `MaxThreads` em `[1, 32]`. `ExportRequest` captura a intenção validada (connector,
archive-alvo opaco, política) para Passos futuros — **nenhum export real ocorre neste Passo** (STOP-THE-LINE).

## Arquitetura do slice

```
Application.EnterpriseVault.Connector    (casos de uso: Issue/Register/SubmitCapability/SubmitInventory)
        │ depende de portas (Contracts), nunca de SQL/PowerShell concreto
        ▼
Contracts.EnterpriseVault.Connector      (IEnrollmentTokenStore, IConnectorRegistry,
                                           IConnectorCapabilityStore, IConnectorInventoryStore,
                                           IEvInventoryAdapter)
        │ implementado por
        ▼
Infrastructure.EnterpriseVault.Connector (Sql*Store — ADO.NET puro, TenantConnectionFactory)
        ▲
        │ tipado por
Domain.EnterpriseVault.Connector         (ConnectorId, EnrollmentToken, ConnectorIdentity,
                                           ConnectorSupportMatrix, ConnectorCapabilityHandshake,
                                           InventorySnapshot, ExportRequestPolicy)
```

**Substituibilidade**: `IEvInventoryAdapter` é a única porta que um Passo futuro precisa implementar de
verdade (com PowerShell real, sob o Worker/host do connector) para transformar este Passo em coleta real —
nenhum tipo do fornecedor cruza a fronteira, o adapter devolve apenas `EvInventoryProbeResult` normalizado.
Nenhum novo Worker/queue é registrado neste Passo (ver [Operação](#operação-runbook-do-passo-1)); os casos
de uso são diretamente invocáveis, mesmo padrão do Passo 3 do Slice 4B antes da integração de fila.
`DependencyRuleTests`/`VendorBoundaryTests` (Architecture.Tests) já cobrem Domain/Contracts/Application sem
mudança — as regras são por projeto, não por bounded context.

## Modelo de dados (migration `0023_slice4c_ev_connector_foundation.sql`, aditiva)

Cinco tabelas novas, todas participantes de `rls.tenant_isolation_policy`:

- `dbo.ev_connector_enrollment_tokens` — `token_hash` é `UNIQUE` GLOBAL (não escopado por tenant): é a
  ÚNICA credencial de resgate, localizada antes de o tenant ser conhecido. `CK_..._ttl` reforça no BANCO a
  janela máxima de 15 minutos (defesa em profundidade da mesma regra do Domain). Campos de consumo
  (`consumed_at_utc`/`consumed_by_connector_id`) só existem quando `status = Consumed` (`CK_..._consumed_fields`).
- `dbo.ev_connectors` — `UQ_ev_connectors_thumbprint (tenant_id, project_id, thumbprint)` é o backstop de
  idempotência do registro; `UQ_ev_connectors_scope` existe para o FK composto dos filhos (mesmo padrão de
  `UQ_pst_partition_plan_parts_scope`, Slice 4B Passo 3).
- `dbo.ev_connector_capability_handshakes` — append-only; `CK_ev_cch_export_capable` reforça no BANCO que
  `export_capable` é sempre derivado de `support_level = Certified AND powershell_snapin_available = 1` —
  a MESMA regra do Domain (`ConnectorCapabilityHandshake.Evaluate`), nenhuma camada confia cegamente na
  outra (mesmo padrão de `CK_pst_partition_executions_byte_identical`, Slice 4B Passo 3).
- `dbo.ev_connector_inventory_snapshots` — append-only e versionado; `UX_ev_cis_connector_version UNIQUE
  (connector_id, version)` é o backstop de concorrência.
- `dbo.ev_connector_inventory_archives` — filha append-only de snapshots; carrega apenas metadados
  operacionais (nunca conteúdo de mailbox).

Migrations `0001`–`0022` permanecem byte-for-byte intactas (`MigrationHashTests.Migration0023AppliesCleanlyAndPriorHashesRemainStable`).
Grants: `ab_app_role` recebe `SELECT, INSERT` em todas, `UPDATE` das colunas mutáveis específicas em
`ev_connector_enrollment_tokens`/`ev_connectors`; as tabelas de evidência append-only (handshakes,
snapshots, archives) não recebem `UPDATE`/`DELETE` algum. `ab_maintenance_role` recebe **apenas** `SELECT`
em `ev_connector_enrollment_tokens` (ver [Idempotência, concorrência e órfãos](#idempotência-concorrência-e-órfãos)).

## Idempotência, concorrência e órfãos

- **Resgate de token sob corrida**: a identidade de MANUTENÇÃO (`ab_maintenance_role`) é usada
  ESTRITAMENTE SOMENTE LEITURA para localizar o token pelo hash — a mesma restrição já aprovada para
  `SqlEvDiscoveryPendingScopeReader` (Slice 3): nunca grava efeito de negócio. O CONSUMO em si é um
  `UPDATE` CONDICIONAL (`WHERE status = Issued`) sob a identidade do tenant já resolvido pela leitura; o
  `rowcount` do `UPDATE`, não a leitura anterior, é a autoridade final de "uso único" — uma leitura
  desatualizada sob corrida nunca produz um segundo consumo (comprovado por
  `RedeemingTheSameTokenTwiceIsARejectedReplay`).
- **Registro idempotente**: `SqlConnectorRegistry.RegisterAsync` procura por thumbprint sob lock
  (`UPDLOCK, HOLDLOCK`) antes de inserir; uma corrida perdida contra outra instalação concorrente do MESMO
  thumbprint (violação de `UQ_ev_connectors_thumbprint`) é reconciliada relendo na próxima tentativa —
  nunca duplica identidade (`ReRegisteringTheSameThumbprintConvergesWithoutDuplicatingIdentity`).
- **Inventário sob corrida (AB-4C-002)**: duas submissões concorrentes do MESMO connector podem calcular a
  MESMA próxima versão com conteúdo DIFERENTE — a violação de `UX_ev_cis_connector_version` sozinha NUNCA
  é tratada como réplay. `SqlConnectorInventoryStore.AppendAsync` relê o snapshot já persistido nessa versão
  e compara semanticamente (`SnapshotHash`): hash igual converge para a linha já gravada
  (`Created = false`, réplay idêntico genuíno —
  `ConcurrentAppendsOfTheSameConnectorVersionWithIdenticalContentConvergeToOneRowWithoutDuplicating`); hash
  diferente lança `ConcurrencyException` em vez de mascarar a mudança perdida como réplay
  (`ConcurrentAppendsOfTheSameConnectorVersionWithDifferentContentSurfaceAnExplicitConcurrencyConflict`).
  `SubmitInventorySnapshotUseCase` releé o latest e tenta de novo com a próxima versão livre, até
  `MaxConvergenceAttempts` tentativas (falha fechado com `ConcurrencyException` se a contenção nunca
  convergir) — nenhuma mudança real é descartada em silêncio, comprovado sob corrida real contra SQL Server
  (`ConcurrentSubmissionsOfDifferentInventoriesFromTheSameLatestBothPersistAtDistinctVersions`,
  `ConcurrentSubmissionsOfIdenticalInventoriesConvergeToASingleLogicalSnapshot`).
- **Evidência de inventário persistida adulterada/corrompida (AB-4C-003)**: a corrida entre writers
  (AB-4C-002) protege quem ESCREVE; nada garantia, até então, que quem LÊ de volta obtinha exatamente o que
  foi gravado — `archive_count` era um campo persistido nunca conferido. `InventorySnapshot.Rehydrate`
  passou a reaplicar o mesmo caminho de canonicalização/deduplicação de `Create` sobre os archives filhos
  REALMENTE carregados, validar `archive_count` contra essa quantidade e recomputar `ComputeHash`
  comparando-o com o `snapshot_hash` gravado. Filho alterado/removido, hash forjado ou contagem divergente
  — qualquer um desses cenários faz `SqlConnectorInventoryStore.GetLatestAsync` e o réplay idempotente do
  caso de uso falharem fechado com `InventorySnapshotIntegrityViolationException`, comprovado sob SQL Server
  real corrompendo a linha por fora da aplicação (identidade administrativa; `ab_app_role` só tem
  `SELECT`/`INSERT` nestas tabelas):
  `GetLatestFailsClosedWhenPersistedArchiveCountDivergesFromLoadedChildren`,
  `GetLatestFailsClosedWhenAChildArchiveIsRemovedButTheStoredHashAndCountStayStale`,
  `GetLatestFailsClosedWhenAChildArchiveIsAlteredButTheStoredHashAndCountStayStale`,
  `GetLatestFailsClosedWhenTheStoredSnapshotHashIsForgedButChildrenStayIntact`. Um snapshot íntegro continua
  round-trip normalmente (`GetLatestOfAnUntamperedSnapshotStillRoundTripsAfterTheIntegrityChecksWereAdded`),
  e a concorrência do AB-4C-002 permanece verde sem alteração.

## Segurança e minimização de PII (delta sobre o Passo 3 do Slice 4B)

- **Anti-IDOR**: toda leitura escopada (`IConnectorRegistry.GetAsync`, `IConnectorCapabilityStore.GetLatestAsync`,
  `IConnectorInventoryStore.GetLatestAsync`) filtra por `project_id` explicitamente além da RLS por tenant;
  um `ConnectorId` de outro tenant/projeto é indistinguível de inexistente
  (`GetFromAnotherProjectIsIndistinguishableFromNotFound`, `InventoryIsIsolatedAcrossProjectsOfTheSameTenant`).
- **Nenhum escopo confiado do cliente em operações connector-iniciadas**: `SubmitConnectorCapabilityHandshakeRequest`/
  `SubmitInventorySnapshotRequest` recebem `TenantScope` já resolvido pelo composition root do transporte
  autenticado do connector (mesmo padrão de `IPortalScopeAccessor` para principals do Portal) — nunca
  informado livremente pelo próprio connector.
- **Nenhum segredo persistido**: o segredo bruto do enrollment token existe apenas na resposta de emissão;
  o Domain e o banco só veem `Sha256Hash`. A chave privada do connector nunca é conhecida pelo Control Plane.
- **Sanitização na forma**: hostname/site/versão passam por `TextValue.Require` (rejeita vazio, caractere
  de controle, excesso de tamanho); o thumbprint é validado como 64 hex minúsculos antes de qualquer uso.
- **SQL injection**: todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de
  entrada.

## Operação (runbook do Passo 1)

Nenhum Worker/queue é registrado neste Passo — os quatro casos de uso são diretamente invocáveis pelo
composition root que os compuser (Control Plane ou o host do connector, conforme a operação); a integração
com fila durável (mesmo padrão de `IEvDiscoveryCommandInbox`) e a autenticação real do transporte do
connector (mTLS/workload identity, §15.1 item 25) ficam para um Passo futuro — ver
[Limitações residuais](#limitações-residuais-para-passos-futuros).

## Critérios de aceite (mapeamento para AB-4C-001)

| # | Critério | Onde é provado |
| --- | --- | --- |
| 1 | Enrollment token é one-time, expira em ≤15 min, não pode ser reutilizado | `IssueRejectsWindowLongerThanMaxTtl`, `ConsumeTwiceIsARejectedReplay`, `RedeemingTheSameTokenTwiceIsARejectedReplay` |
| 2 | Identidade vinculada a tenant/projeto e material público; private key nunca persistida | `RegisterConnectorHappyPathDerivesScopeEntirelyFromTheToken`, `ThumbprintAccepts64LowercaseHexCharacters` |
| 3 | Enrollment/registro inválido, replay, tenant mismatch ou identity mismatch falha fechado | `RegisterConnectorMalformedSecretIsIndistinguishableFromUnknownToken`, `RegisterConnectorUnknownSecretThrowsNotFound` |
| 4 | Capability discovery incompatível impede export capability com diagnóstico estruturado | `HandshakeWithUnknownVersionBlocksExportWithSchemaUnknownDiagnostic`, `HandshakeWithCompatibleFamilyButNoSnapinBlocksExport` |
| 5 | Inventory adapter atrás de interface; Domain/Application sem tipos PowerShell/EV/vendor | `IEvInventoryAdapter` (Contracts, sem dependência de vendor); `VendorBoundaryTests`/`DependencyRuleTests` inalterados |
| 6 | Snapshot determinístico, hashado, scoped e versionado; replay não duplica; leitura falha fechado sobre evidência persistida adulterada/corrompida (AB-4C-003) | `HashIsDeterministicRegardlessOfInputOrder`, `SubmitInventorySnapshotIdenticalResubmissionIsAnIdempotentReplayWithoutANewRow`, `RehydrateFailsClosedWhenStoredHashDoesNotMatchTheLoadedChildren`, `RehydrateFailsClosedWhenArchiveCountDoesNotMatchTheLoadedChildren`, `GetLatestFailsClosedWhenTheStoredSnapshotHashIsForgedButChildrenStayIntact`, `GetLatestFailsClosedWhenAChildArchiveIsRemovedButTheStoredHashAndCountStayStale` |
| 7 | Mudança real gera nova versão sem reescrever evidência anterior | `SubmitInventorySnapshotChangedArchivesCreatesANewVersionWithoutRewritingThePrevious`, `GetLatestReturnsTheHighestVersionAcrossMultipleSnapshots` |
| 8 | Nenhum path/credential/token/PII/conteúdo de item em logs/evidência | `InventoryArchiveRecord` (campos fechados, sanitizados); ver [Segurança e minimização de PII](#segurança-e-minimização-de-pii-delta-sobre-o-passo-3-do-slice-4b) |
| 9 | Desenho outbound-only; nenhum SMB/inbound Control Plane → EV introduzido | Nenhum novo endpoint/porta inbound; `IEvInventoryAdapter` roda perto do dado (contrato) |
| 10 | ExportRequest/Policy valida limites; nenhum export real ocorre | `CreateRejectsLimitsOutsideTheDocumentedEnvelope`, `CreateAcceptsTheDocumentedBoundaryValues`, `DefaultPolicyMatchesTheRunbookDefaults` |
| 11 | Cross-tenant/cross-project/forged connector negados sem revelar existência | `GetFromAnotherProjectIsIndistinguishableFromNotFound`, `RevokingATokenFromTheWrongProjectIsIndistinguishableFromNotFound`, `InventoryIsIsolatedAcrossProjectsOfTheSameTenant` |
| 12 | Migrations anteriores byte-for-byte; novas migrations passam hash/determinismo e least-privilege | `MigrationHashTests.Migration0023AppliesCleanlyAndPriorHashesRemainStable` |
| 13 | CI completo verde no HEAD final | `dotnet test` (1148/1148), `dotnet format --verify-no-changes`, SCA, `git diff --check` |

## Fora do escopo — STOP-THE-LINE

Este PR NÃO implementa: execução real de `Export-EVArchive`; disparo real de `Get-EVArchive` contra
ambiente de cliente; armazenamento de credenciais EV no Control Plane; inbound SMB/RPC/WinRM do Control
Plane para host EV; Outlook/COM automation; export NATIVE/EML real; split/repair de PST; AzCopy/Azure
staging/SAS; Purview, Graph, Exchange Online ou import job; delta/freeze operacional real; descomissionamento
de EV.

## Limitações residuais (para Passos futuros)

- `IEvInventoryAdapter` não tem implementação real de `Get-EVArchive` nesta fatia — apenas o contrato,
  exercitado por duplos de teste determinísticos. Uma implementação PowerShell real, hospedada perto do
  dado, é trabalho de um Passo futuro.
- A autenticação real do transporte do connector (mTLS/certificado de cliente curto, §15.1 itens 23-26) não
  é implementada: os casos de uso connector-iniciados recebem `ConnectorId`/`TenantScope` já resolvidos,
  deixando explícito no contrato (`IConnectorRegistry.GetAsync`) que a resolução real cabe a um composition
  root de transporte futuro — mesmo padrão hoje usado por `IPortalScopeAccessor` para o Portal.
- Revogação de identidade de connector (`ConnectorIdentity.Revoke`) existe no Domain mas não tem um caso de
  uso/endpoint dedicado nesta fatia — o work order exige revogação de TOKEN (implementada), não de
  connector; o estado `ConnectorRegistrationStatus.Revoked` e a rejeição fail-closed de reinstalação sobre
  ele já estão provados no Domain (`ReRegisterARevokedConnectorFailsClosedNeverReactivatesSilently`).
- Nenhuma integração com a fila durável de Jobs (`IJobStore`/command inbox) foi feita — os quatro casos de
  uso são diretamente invocáveis, sem Worker/queue, mesmo padrão inicial do Passo 3 do Slice 4B.

## Regra de encerramento

Este PR permanece **Draft** durante toda a implementação. Não marcar Ready nem fazer merge sem
`ARCHIVEBRIDGE_MERGE_APPROVED` para o HEAD corrente do Engineering Reviewer, com CI totalmente verde.
