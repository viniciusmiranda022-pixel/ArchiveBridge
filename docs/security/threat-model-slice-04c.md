# Threat model — Slice 4C, Passo 1 (EV Connector Enrollment & Inventory Foundation)

Delta sobre o modelo de ameaças da plataforma. Escopo: fundação segura de enrollment/identidade do Source
Connector, capability handshake contra a matriz de suporte e inventário read-only versionado. Sem
`Export-EVArchive`, sem `Get-EVArchive` contra ambiente de cliente, sem credencial EV no Control Plane, sem
inbound Control Plane → EV, sem Outlook/COM automation nesta fatia (ver STOP-THE-LINE em
[`vertical-slice-04c-ev-connector-foundation.md`](../engineering/vertical-slice-04c-ev-connector-foundation.md)).

## Ativos adicionais

- **Enrollment tokens**: hash SHA-256 de um segredo de uso único (o segredo bruto NUNCA é persistido — só
  existe na resposta de emissão), janela de validade, estado (Issued/Consumed/Revoked) e o connector que o
  consumiu. Metadados operacionais de curta duração, não conteúdo de mailbox.
- **Identidade de connector**: `connector_id` opaco, thumbprint de chave PÚBLICA (nunca a privada),
  hostname/site sanitizados e versão. A chave privada é gerada e mantida exclusivamente no host do
  connector (TPM/Certificate Store, §15.1 item 22); o Control Plane nunca a vê.
- **Capability handshakes**: versão EV observada, disponibilidade do snap-in PowerShell, nível de suporte
  resolvido pela matriz e o diagnóstico estruturado do bloqueio de export capability — evidência
  operacional, não conteúdo de mailbox.
- **Snapshots de inventário**: identidade externa opaca do archive, tipo, vault store (quando permitido) e
  status, com hash determinístico e versão. **Não** há assunto, corpo, remetente, destinatário, anexo ou
  qualquer valor extraído do conteúdo do mailbox nestas tabelas — o adapter deste Passo é substituível e
  nenhuma implementação real percorre item de mensagem.

## Classificação de dados

As tabelas novas (`ev_connector_enrollment_tokens`, `ev_connectors`, `ev_connector_capability_handshakes`,
`ev_connector_inventory_snapshots`, `ev_connector_inventory_archives`) **não são "zero PII"**: hostname,
site e identidade externa de archive são metadados operacionais atribuíveis a um ambiente/connector
específico. O que elas **não** contêm: segredo bruto do enrollment token (só o hash SHA-256), chave privada
do connector, credencial EV, transcript PowerShell bruto, ou qualquer campo de conteúdo de mailbox — o
`InventoryArchiveRecord` (Domain) tem um construtor fechado que só aceita os campos normalizados
documentados, tornando estruturalmente impossível persistir um campo de conteúdo por engano.

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| Replay do enrollment token | O resgate é um `UPDATE` CONDICIONAL (`WHERE status = Issued`) — o `rowcount`, não uma leitura anterior, decide se o resgate teve efeito; um segundo resgate concorrente do MESMO token sempre observa `rowcount = 0` e falha com `EnrollmentTokenNotConsumableException`. Comprovado por `RedeemingTheSameTokenTwiceIsARejectedReplay`. |
| Enrollment token de vida longa | `EnrollmentToken.Issue` recusa fail-closed qualquer janela acima de `MaxTtl` (15 minutos); o banco reforça o mesmo limite via `CK_ev_connector_enrollment_tokens_ttl` (defesa em profundidade — nenhuma camada confia cegamente na outra). Comprovado por `IssueRejectsWindowLongerThanMaxTtl`. |
| Enumeração de enrollment tokens (probing de hash) | O resgate devolve o MESMO erro (`EnrollmentTokenNotFoundException`) para hash inexistente, token expirado, consumido ou revogado — nenhuma resposta distingue "não existe" de "existe mas inválido". Comprovado por `RedeemingAnUnknownHashIsIndistinguishableFromAnyOtherInvalidToken`. |
| Escalação via identidade de manutenção | `SqlEnrollmentTokenStore` usa a identidade de MANUTENÇÃO estritamente SOMENTE LEITURA (só `GRANT SELECT`, nenhum `UPDATE`/`INSERT`/`DELETE` concedido) para localizar o token pelo hash; o consumo em si é um `UPDATE` sob a identidade do TENANT já resolvido. `EvWorkerBoundaryTests.MaintenanceIdentityIsRestrictedToApprovedCrossTenantInfrastructureOperations` allowlista explicitamente os únicos dois arquivos autorizados a abrir conexão de manutenção neste bounded context. |
| Vazamento cross-tenant/cross-project (IDOR) | Toda leitura escopada (`IConnectorRegistry.GetAsync`, `IConnectorCapabilityStore.GetLatestAsync`, `IConnectorInventoryStore.GetLatestAsync`) participa de `rls.tenant_isolation_policy` (FILTER + BLOCK) e filtra `project_id` explicitamente; um `ConnectorId`/`EnrollmentTokenId` de outro tenant/projeto é indistinguível de inexistente. Comprovado por `GetFromAnotherProjectIsIndistinguishableFromNotFound`, `RevokingATokenFromTheWrongProjectIsIndistinguishableFromNotFound`, `InventoryIsIsolatedAcrossProjectsOfTheSameTenant`. |
| Escopo forjado por um connector | Operações connector-iniciadas (`SubmitConnectorCapabilityHandshakeRequest`/`SubmitInventorySnapshotRequest`) recebem `TenantScope` já resolvido pelo composition root do transporte autenticado — nunca um valor enviado pelo próprio connector; o registro inicial deriva o escopo inteiramente do enrollment token resgatado, nunca de um campo de request. |
| Reativação silenciosa de connector revogado | `ConnectorIdentity.ReRegister` lança `ConnectorRevokedException` fail-closed quando `Status != Active` — uma reinstalação nunca reativa um connector revogado. Comprovado por `ReRegisterARevokedConnectorFailsClosedNeverReactivatesSilently`. |
| Export capability concedida por engano (schema/versão desconhecida) | `ConnectorCapabilityHandshake.Evaluate` (Domain) é a ÚNICA fonte de `ExportCapable`; o valor default para qualquer versão não reconhecida é `Unknown` (nunca tratado como suportado — regra "UNKNOWN não é SUPPORTED"), e nenhuma família começa `Certified` na matriz embarcada (honestidade comercial, ADR-0013). Reforçado no BANCO por `CK_ev_cch_export_capable`, a MESMA fórmula do Domain. Comprovado por `HandshakeWithUnknownVersionBlocksExportWithSchemaUnknownDiagnostic`, `NoFamilyIsCertifiedByDefaultHonestyRule`. |
| Corrida de registro (duas instalações concorrentes do mesmo thumbprint) | `UQ_ev_connectors_thumbprint (tenant_id, project_id, thumbprint)` é o backstop SQL; `SqlConnectorRegistry.RegisterAsync` reconcilia relendo sob violação de unicidade, nunca duplica identidade nem falha por corrida transitória. Comprovado por `ReRegisteringTheSameThumbprintConvergesWithoutDuplicatingIdentity`. |
| Corrida de submissão de inventário (mesma versão calculada por dois envios concorrentes) | `UX_ev_cis_connector_version UNIQUE (connector_id, version)` é o backstop SQL. A colisão de versão sozinha NUNCA autoriza convergir: `SqlConnectorInventoryStore.AppendAsync` relê a linha já persistida nessa versão e só trata como réplay se o `SnapshotHash` for IGUAL ao do candidate; hash diferente lança `ConcurrencyException` e `SubmitInventorySnapshotUseCase` retenta contra o novo latest (limite de tentativas, fail-closed). Comprovado por `ConcurrentAppendsOfTheSameConnectorVersionWithIdenticalContentConvergeToOneRowWithoutDuplicating`, `ConcurrentAppendsOfTheSameConnectorVersionWithDifferentContentSurfaceAnExplicitConcurrencyConflict`, `ConcurrentSubmissionsOfDifferentInventoriesFromTheSameLatestBothPersistAtDistinctVersions`, `ConcurrentSubmissionsOfIdenticalInventoriesConvergeToASingleLogicalSnapshot`. |
| Perda silenciosa de mudança de inventário sob corrida (AB-4C-002) | Uma mudança real cujo `candidateVersion` foi ocupado por outro writer com conteúdo DIFERENTE jamais é devolvida como `Created:false` apontando para o snapshot do concorrente (o que descartaria evidência sem qualquer sinal). A comparação é sempre semântica (`SnapshotHash`), e o caminho retriable preserva a mudança em uma nova versão; a versão do concorrente permanece intacta (append-only, evidência anterior nunca reescrita). |
| Duplicidade de archive dentro do mesmo snapshot | `InventorySnapshot.Create` recusa IDs de archive duplicados ANTES de computar qualquer hash (`DuplicateInventoryArchiveException`) — nenhum snapshot inconsistente chega a ser persistido. |
| Evidência de inventário persistida adulterada/corrompida sendo lida como canônica (AB-4C-003) | A persistência é fronteira NÃO CONFIÁVEL: `InventorySnapshot.Rehydrate` reaplica a canonicalização/deduplicação de `Create` sobre os archives filhos REALMENTE carregados, valida `archive_count` do header contra essa quantidade (antes dado morto) e recomputa `ComputeHash` comparando-o com o `snapshot_hash` gravado. Archive filho alterado/removido, `snapshot_hash` forjado ou `archive_count` divergente fazem `SqlConnectorInventoryStore.GetLatestAsync` — e o réplay idempotente do caso de uso, que releé o mesmo latest — falhar fechado com `InventorySnapshotIntegrityViolationException`, em vez de classificar a corrupção como réplay idêntico. Comprovado sob SQL Server real corrompendo a linha por fora da aplicação: `GetLatestFailsClosedWhenPersistedArchiveCountDivergesFromLoadedChildren`, `GetLatestFailsClosedWhenAChildArchiveIsRemovedButTheStoredHashAndCountStayStale`, `GetLatestFailsClosedWhenAChildArchiveIsAlteredButTheStoredHashAndCountStayStale`, `GetLatestFailsClosedWhenTheStoredSnapshotHashIsForgedButChildrenStayIntact`. |
| Codec ambíguo de `CapabilityDiagnostics` mascarando mudança real como corrupção, ou permitindo truncamento silencioso (AB-4C-004) | O codec antigo unia/separava diagnósticos por `';'` — imprimível e válido dentro de um código — então um único diagnóstico `"EV;CODE"` voltava da persistência como dois códigos, recompunha hash diferente do gravado e a reidratação (AB-4C-003) classificava incorretamente como corrupção um snapshot que a aplicação gravou corretamente; além disso, 20 códigos de 50 chars unidos por `';'` geravam 1019 chars para uma coluna `nvarchar(1000)`, arriscando truncamento silencioso na escrita. `InventoryArchiveRecord` agora usa `DiagnosticsPersistenceDelimiter` (U+001F, caractere de controle que `TextValue.Require` recusa em qualquer código) — lossless por construção — canonicaliza (ordena + deduplica) a lista no construtor compartilhado por `Create`/`Rehydrate`, e recusa qualquer agregado cuja representação canônica exceda a coluna persistida ANTES de aceitar o valor. Um payload persistido malformado (delimitador duplicado) falha fechado na leitura com `InventorySnapshotIntegrityViolationException`, nunca é tratado como lista canônica ambígua. Comprovado por `CapabilityDiagnosticsContainingTheOldDelimiterRoundTripsLosslessThroughEncodeAndDecode`, `SnapshotHashConvergesRegardlessOfCapabilityDiagnosticsOrderWithinAnArchive`, `CapabilityDiagnosticsExceedingThePersistedRepresentationLimitIsRejected`, `GetLatestFailsClosedWhenThePersistedCapabilityDiagnosticsFieldIsMalformed`. |
| Vazamento de segredo em log/evidência | O segredo bruto do enrollment token nunca é passado a um logger nem persistido — só `Sha256Hash` cruza para Domain/Infrastructure; `ConnectorPublicKeyThumbprint` valida a FORMA (64 hex minúsculos) antes de qualquer uso, e nenhum tipo do Domain aceita um valor de chave privada. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada. |

## Fora de escopo desta fatia (herdado do STOP-THE-LINE)

Export-EVArchive, Get-EVArchive contra ambiente de cliente, armazenamento de credencial EV no Control
Plane, inbound SMB/RPC/WinRM Control Plane → EV, Outlook/COM automation, export NATIVE/EML real,
split/repair de PST, AzCopy/Azure staging/SAS, Purview/Graph/Exchange Online/import job, delta/freeze
operacional real, descomissionamento de EV. Nenhum destes fluxos existe no código deste Passo — não há
superfície de ameaça nova a analisar para eles aqui.

# Delta — Slice 4C, Passo 2 (EV Export Command & Throttling Foundation)

Delta sobre o modelo de ameaças acima. Escopo: fundação de EXECUÇÃO de export — comando, throttling,
idempotência, captura de resultado, manifesto e custody de output — ainda **sem** execução real contra
ambiente EV de cliente (AB-4C-005). O adapter concreto de processo/PowerShell existe em Infrastructure,
mas nenhum teste deste Passo o executa contra um Enterprise Vault real (STOP-THE-LINE preservado).

## Ativos adicionais

- **Pedidos de exportação** (`ev_export_requests`): identidade opaca do pedido, connector, identidade
  externa opaca do archive-alvo, `MaxThreads`/`MaxPSTSizeMB` efetivos e a chave de idempotência CANÔNICA
  (derivada, nunca aleatória). Metadados operacionais, não conteúdo de mailbox.
- **Slots de throttling** (`ev_export_connector_throttle_slots`, `ev_export_archive_throttle_slots`,
  AB-4C-007): vínculo transitório e RECUPERÁVEL de exclusividade por connector e por archive, com deadline
  durável e identidade do titular — nenhum dado de conteúdo.
- **Tentativas de exportação** (`ev_export_attempts`): desfecho estruturado, código de resultado,
  timestamps, exit code do processo e (quando concluída) a versão do engine/connector. Evidência
  operacional histórica, append-only.
- **Manifesto canônico** (`ev_export_manifest_entries`): identidade do output PST (derivada do HASH do
  conteúdo — nunca do nome de arquivo), tamanho e SHA-256, calculados após o fechamento do arquivo.
- **Itens oversized** (`ev_export_oversized_items`): referência OPACA emitida pelo exporter, tamanho e
  classificação (§16.4) — nunca assunto/remetente/destinatário/corpo/anexo.
- **Eventos de custódia/auditoria** (`ev_export_events`): código do evento (requested/started/throttled/
  retry/completed/failed/output-discovered/oversized-detected/integrity-failed), sem conteúdo sensível.

## Classificação de dados

As SETE tabelas novas (`ev_export_requests`, `ev_export_connector_throttle_slots`,
`ev_export_archive_throttle_slots`, `ev_export_attempts`, `ev_export_manifest_entries`,
`ev_export_oversized_items`, `ev_export_events`) **não são "zero PII"**: a
identidade externa de archive e os hashes/tamanhos de output são metadados operacionais atribuíveis a um
pedido/tentativa específicos, na mesma classificação das tabelas do Passo 1. O que elas **não** contêm:
conteúdo de mailbox (assunto, corpo, anexo), credencial EV, token, private key, transcript PowerShell
bruto ou caminho físico absoluto — o local físico do output é sempre DERIVADO em tempo de leitura, a
partir de IDs opacos (tenant/projeto/connector/pedido/tentativa) mais a raiz local autorizada configurada
no host do connector; nenhum caminho é persistido. `EvExportManifestEntry`/`EvOversizedNativeItem`
(Domain) têm construtores fechados que só aceitam os campos normalizados documentados — estruturalmente
impossível persistir um campo de conteúdo por engano, mesmo tipo de garantia já usada em
`InventoryArchiveRecord` (Passo 1).

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| Command injection no comando `Export-EVArchive` | `EvExportProcessScript.Script` é uma constante FIXA — nunca concatenada com dado da requisição. Todo dado (`ArchiveId`, `OutputDirectory`, `MaxThreads`, `MaxPSTSizeMB`) entra EXCLUSIVAMENTE como valor de variável de ambiente (`EV_EXPORT_ARG_*`), lido pelo script via `$env:` e nunca reinterpretado como código (nenhum `Invoke-Expression`). Mesmo padrão já hardenado em `EvPowerShellCommandBuilder` (Discovery, Slice 3). Comprovado por `MaliciousArchiveIdNeverEntersTheProcessArgumentListOrScriptText` contra um corpus de `;`, `&&`, pipes, backtick, `$()`, `${}`, aspas e CR/LF. |
| Path traversal/escape de containment do diretório de saída (§15.2) | `EvExportOutputLocation.RelativeToken` é SEMPRE derivado exclusivamente de IDs opacos do servidor (`EvExportOutputDescriptor`) — nunca de `ExternalArchiveId` ou de qualquer valor do cliente, eliminando estruturalmente a superfície de traversal a partir de entrada do usuário. `EvExportOutputPaths.ResolvePhysicalDirectory` reforça com `ArtifactPathContainment.EnsureContained` (mesmo helper hardenado do Passo 1/Slice 4B): rejeita `..`, caminhos absolutos, prefixo semelhante ("root_evil" ≠ "root") e symlink/junction/reparse point em qualquer ponto da cadeia. Comprovado por `RelativeTokenEscapingWithDotDotIsRejected`, `RelativeTokenEscapingWithAbsolutePathIsRejected`, `ASiblingDirectoryWithASimilarPrefixIsNeverTreatedAsContained`, `ASymlinkInTheChainIsRejected`. |
| Raiz de output UNC não allowlisted | `EvExportOutputRootOptions.Validate()` recusa fail-closed no startup quando a raiz configurada é UNC e não consta de `AllowedUncRoots` — nenhum efeito de exportação é registrado. Comprovado por `AnUncRootNotOnTheAllowlistFailsClosedAtStartup`. |
| Capability concedida/perdida entre o enfileiramento e a execução | `EvExportCommandProcessor.DispatchAsync` REVALIDA, a cada tentativa, `IConnectorRegistry.GetAsync` (connector ativo) e `IConnectorCapabilityStore.GetLatestAsync` (handshake vigente ExportCapable) — nunca confia no estado observado no momento de `RequestEvExportUseCase`. Bloqueio é TERMINAL (nunca retry automático); o executor NUNCA é invocado neste caminho. Comprovado por `CapabilityRevokedBetweenRequestAndExecutionBlocksFailClosed`. |
| Concorrência não autorizada / duplicidade de efeito lógico | Idempotência do PEDIDO é por identidade CANÔNICA (`EvExportRequestIdentity`, hash determinístico de tenant/projeto/connector/archive/política) — nunca um token aleatório do cliente; dois pedidos logicamente idênticos convergem para o MESMO `ExportRequestId`/Job (backstop SQL: `UQ_ev_export_requests_idempotency`). Throttling de EXECUÇÃO é por `ev_export_connector_throttle_slots`/`ev_export_archive_throttle_slots`: aquisição atômica por slot (livre OU expirado) sob `UPDLOCK, HOLDLOCK`, cobrindo os DOIS slots na MESMA transação — nenhuma aquisição parcial. Comprovado por `DuplicateConcurrentRequestsWithTheSameCanonicalIdentityConvergeToOneLogicalRequest`, `AcquiringTheSameConnectorTwiceIsThrottled`, `AcquiringTheSameArchiveTwiceIsThrottledEvenFromDifferentConnectors`, `TwoConcurrentAcquisitionsAfterExpiryProduceExactlyOneWinner`. |
| Lease de throttling órfão permanentemente após queda do worker (AB-4C-007 blocker 1) | Cada slot carrega um deadline durável (`expires_at_utc`); a PRÓPRIA aquisição seguinte recupera transparentemente um slot cujo titular anterior expirou — nenhum reaper/lock em memória separado é necessário. Durante uma tentativa em curso, `EvExportCommandProcessor` renova o lease de throttle SOB O MESMO batimento periódico do lease do Job (`PlanningHeartbeat.onBeatAsync`); perda da renovação cerca a operação (`Fenced`) antes de qualquer efeito novo. Liberação em cleanup usa um token BOUNDED independente do token (possivelmente já cancelado) da operação — nunca depende dele, e nunca mascara a exceção original. Comprovado por `AnExpiredLeaseIsReclaimedByANewAcquisitionEvenWithoutAnExplicitRelease`, `ALeaseStillWithinItsDeadlineIsNeverStolen`, `AnOldOwnerCannotReleaseOrRenewALeaseAlreadyReassumedByANewOwner`, `LosingTheThrottleLeaseDuringExecutionFencesTheOperationEvenWithAHealthyJobLease`. |
| Tentativa throttled desaparecendo do evidence chain / retry sem auditoria (AB-4C-007 blockers 2/3) | Toda tentativa bloqueada por throttle é persistida no attempt history append-only (mesmo fencing do restante do pipeline) — nunca só um evento de auditoria solto. `RetryScheduled` é auditado sempre que o Job efetivamente transita para retry (Applied/IdempotentReplay) — nunca quando o cercamento foi perdido (Fenced), evitando duplicação/evidência ambígua. Comprovado por `AThrottledAttemptIsPersistedWithRetryScheduledAuditedAndTheRetryPreservesLineage`, `RetryScheduledIsAlsoAuditedForATransientProcessFailure`. |
| Replay de resultado concluído aceito sem revalidação física | `GetEvExportResultUseCase` NUNCA devolve o manifesto persistido sem antes rechamar `IEvExportOutputInspector.ScanPstOutputsAsync` e comparar o conjunto físico (contagem + hash + tamanho) contra o manifesto — arquivo ausente, hash divergente ou contagem inconsistente lança `EvExportIntegrityViolationException` fail-closed. Comprovado por `ReplayWithARemovedOutputFailsClosed`, `ReplayWithATamperedHashFailsClosed`, `ReplayAfterTheOutputWasRemovedFailsClosed` (SQL real). |
| Identidade de output baseada em nome de arquivo (spoofing por renomeação) | `EvExportManifestEntry.Id` é SEMPRE derivado do hash SHA-256 + tamanho do CONTEÚDO (`DeriveId`) — nunca do nome de arquivo; `FileNameHint` é preservado apenas como evidência operacional, nunca usado em comparação de identidade. Comprovado por `ManifestEntryIdentityIsDerivedFromContentNeverFromFileName`. |
| Item nativo oversized omitido do certificado final (§16.4) | `EvOversizedNativeItem.Classify` é chamado para TODO item bruto reportado pelo exporter (`EvExportEvaluator.BuildOversizedItems`), independentemente do desfecho da tentativa; o construtor de `EvOversizedNativeItem` recusa (fail-closed) qualquer tamanho ≤ 250 MB como "oversized" — nunca uma classificação por omissão. Persistido em tabela filha append-only própria (`ev_export_oversized_items`), nunca misturado ao manifesto de sucesso. |
| Escalação via identidade de manutenção (enumeração de escopos de export) | `SqlEvExportPendingScopeReader` é o TERCEIRO arquivo explicitamente allowlistado por `EvWorkerBoundaryTests.MaintenanceIdentityIsRestrictedToApprovedCrossTenantInfrastructureOperations` — mesma restrição estritamente READ-ONLY (nenhum claim/UPDATE/INSERT) do leitor de escopos de descoberta (Passo 1/Slice 3), devolve apenas `(tenant, projeto)`. |
| Segredo/transcript bruto vazando para o Control Plane | O envelope JSON emitido pelo script fixo carrega apenas `schemaVersion/success/errorCode/engineVersion/oversizedItems` (referências opacas) — nunca stdout/stderr bruto, nunca o transcript PowerShell local (item 16, protegido no host connector). `ByteLimitedProcessRunner` (reaproveitado do Slice 3) já limita e nunca persiste a captura bruta além da avaliação estruturada do envelope. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão do Passo 1. |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Execução real contra ambiente EV de cliente sem support-matrix certificada e host explicitamente
autorizado, automação Outlook/COM, NATIVE/EML conversion real, delta/freeze operacional real,
descomissionamento EV, AzCopy/Azure staging/SAS, Purview/Graph/Exchange Online/import job, reconciliação
M365. Nenhum destes fluxos existe no código deste Passo — não há superfície de ameaça nova a analisar
para eles aqui.

# Delta — Slice 4C, Passo 3 (EV Delta Strategy & Freeze Planning Foundation)

Delta sobre o modelo de ameaças acima. Escopo: estratégia incremental/delta por versão do EV (watermarks
opacos com lineage), correlação baseline/delta/final-delta com o export foundation do Passo 2 e
planejamento/autorização de freeze/cutover — **sem** executar freeze real, sem alterar retention/policy/
acesso no EV e sem descomissionamento (AB-4C-008). Nenhum comando destrutivo ou mudança operacional real
no Enterprise Vault existe no código deste Passo.

**Correção AB-4C-009 (mesmo Passo, antes de qualquer aprovação de merge):** a revisão de engenharia
encontrou dois desvios fail-closed no HEAD original do Passo 3, corrigidos abaixo e refletidos nas duas
linhas atualizadas da tabela de ameaças/mitigações: (1) o gate de certificação de delta strategy aceitava
`Compatible` como suficiente para execução canônica — deveria exigir `Certified`, nível que nenhuma família
embarcada possui neste Passo; (2) o hash de evidência do watermark (`lineage_hash`) cobria apenas os campos
de escopo (tenant/projeto/connector/archive/fase/strategy), não `opaque_token`/`producing_execution_id`/
`issued_at_utc` — uma linha SQL com qualquer um destes três campos adulterado ainda passava na releitura.

## Ativos adicionais

- **Watermarks** (`ev_watermarks`): identidade opaca, lineage (tenant/projeto/connector/archive/fase/
  strategy/execução que o produziu) e um `opaque_token` interpretável EXCLUSIVAMENTE pelo adapter EV da
  strategy selecionada — nunca decodificado pelo Domain/Application/Control Plane. Metadados operacionais,
  não conteúdo de mailbox.
- **Tentativas de execução de fase** (`ev_delta_attempts`): fase (Baseline/Delta/FinalDelta), strategy
  resolvida (quando elegível), watermark anterior/emitido, desfecho estruturado e motivo de bloqueio —
  append-only, mesma classificação das tentativas de exportação do Passo 2.
- **Planos de freeze** (`ev_freeze_plans`): estado (`FreezeRequired`/`FreezeAuthorized`/`FreezeRejected`/
  `FinalDeltaReady`/`RollbackRetentionRequired`/`DecommissionBlocked`) e, quando autorizado, o operador, o
  role, a justificativa e a correlação — evidência de AUTORIZAÇÃO formal, nunca de execução. Única tabela
  nova deste Passo que é MUTÁVEL (concorrência otimista por `version`), pelo mesmo motivo que os slots de
  throttling do Passo 2 são mutáveis: representa "estado atual", não histórico append-only.
- **Eventos de custódia/auditoria** (`ev_delta_events`): código do evento (strategy-selected/baseline-
  started/completed/delta-requested/completed/failed/watermark-issued/accepted/rejected/freeze-requested/
  authorized/rejected/final-delta-ready/decommission-blocked), sem conteúdo sensível.

## Classificação de dados

As QUATRO tabelas novas (`ev_watermarks`, `ev_delta_attempts`, `ev_freeze_plans`, `ev_delta_events`) **não
são "zero PII"**: a identidade externa de archive e o `opaque_token` do watermark são metadados
operacionais atribuíveis a um archive/execução específicos, mesma classificação das tabelas dos Passos
1-2. O que elas **não** contêm: conteúdo de mailbox, credencial EV, token de acesso, private key ou
transcript PowerShell bruto — é responsabilidade do adapter que EMITE o `opaque_token` garantir isso antes
de entregá-lo ao Domain; `EvWatermark.Issue` (Domain) sanitiza o token (tamanho/controle) mas nunca
inspeciona seu conteúdo interno (é opaco por design, item 3/4 do work order).

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| Delta strategy implícita para versão/schema desconhecida, OU strategy apenas `Compatible`/`Tested` tratada como autorizada para execução canônica (AB-4C-009 item 1) | `EvDeltaStrategySelectionPolicy` nunca infere: versão não reconhecida por nenhuma família da matriz embarcada ⇒ `Unknown`; família reconhecida mas sem strategy `Certified` elegível para a fase ⇒ `Unsupported` (inclui tanto família vetada quanto família apenas `Compatible`/`Tested` — honestidade comercial, mesma regra de `ConnectorCapabilityHandshake`/`ExportCapable`); empate de precedência entre `Certified` elegíveis ⇒ `Ambiguous` — em NENHUM destes três casos o adapter EV é chamado. Como nenhuma família embarcada está `Certified` neste Passo, todo baseline/delta/final-delta real permanece bloqueado até um Passo de certificação futuro. Comprovado por `UnknownOrUnrecognizedVersionSelectsNothing`, `AKnownButNotCertifiedFamilyVersionIsUnsupportedForCanonicalExecutionInEveryPhase`, `ACertifiedDescriptorInjectedIntoThePolicyIsSelectedDeterministically`, `TwoEligibleDescriptorsTiedAtTheHighestPrecedenceAreAmbiguousFailClosed`, `BaselineWithUnknownEvVersionBlocksFailClosedAndRecordsTheAttempt`, `BaselineWithACompatibleOnlyFamilyIsBlockedFailClosedAndIsIdempotentOnRetry`, `BaselineDeltaAndFinalDeltaAreAllBlockedFailClosedOverRealSqlBecauseNoEmbeddedFamilyIsCertifiedYet`. |
| `ReceivedDate` isolado como critério genérico de delta | Nenhum tipo do Domain/Contracts expõe ou aceita `ReceivedDate`; o `opaque_token` do watermark é responsabilidade EXCLUSIVA do adapter da strategy selecionada, nunca um campo estruturado do Domain — estruturalmente impossível ao Domain/Application decidir delta por uma única data. |
| Watermark de outro tenant/projeto/connector/archive aceito como anterior | `EvWatermark.EnsureCanPrecede` compara tenant/projeto/connector/archive do watermark candidato contra o pedido ANTES de qualquer chamada ao adapter; qualquer divergência lança `EvWatermarkRejectedException(CrossScope)` fail-closed. Comprovado por `ACrossScopeWatermarkNeverPrecedesADeltaForAnotherArchive`, `AWatermarkFromAnotherTenantIsRejectedCrossScope`. |
| Watermark de outra strategy ou downgrade de versão aceito silenciosamente | `EnsureCanPrecede` também recusa strategy de nome diferente (`StrategyMismatch`) e versão inferior à do watermark canônico (`StrategyDowngrade`) — nunca combina lineage de strategies distintas nem regride versão. Comprovado por `AWatermarkFromAnotherStrategyIsRejectedAsStrategyMismatch`, `AStrategyVersionDowngradeIsRejected`. |
| Watermark stale/replay aceito como avanço de checkpoint | `EvWatermark.EnsureSucceededBy` exige que o candidato seja ESTRITAMENTE mais recente (`IssuedAtUtc`) que o canônico atual — um candidato igual ou anterior lança `EvWatermarkRejectedException(Stale)` fail-closed, mesmo que o adapter já tenha sido chamado. Comprovado por `AStaleCandidateWatermarkIsRejected`. |
| Evidência de watermark persistida adulterada sendo lida como canônica — incluindo adulteração ISOLADA de `opaque_token`/`producing_execution_id`/`issued_at_utc` (AB-4C-009 item 2), que antes da correção não era coberta pelo hash | A persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de `InventorySnapshot`/`EvExportManifest`): `EvWatermark.Rehydrate` recomputa o hash de evidência a partir de TODOS os campos REALMENTE carregados — tenant/projeto/connector/archive/fase/strategy + `producing_execution_id` + `opaque_token` + `issued_at_utc` (canonicalizado em milissegundos, mesma precisão de `DATETIME2(3)`) — e recusa fail-closed (`EvWatermarkRejectedException(Tampered)`) qualquer divergência do hash persistido; em particular, adulterar `issued_at_utc` de um watermark antigo NUNCA o promove silenciosamente a `LatestCanonical` (`ORDER BY issued_at_utc DESC` só entrega uma linha que ainda passa na revalidação do hash). Comprovado por `RehydrateFailsClosedWhenTheLineageHashDoesNotMatchTheLoadedFields`, `RehydrateFailsClosedWhenOnlyTheOpaqueTokenIsAlteredButTheRestOfTheRowStaysIntact`, `RehydrateFailsClosedWhenOnlyTheProducingExecutionIdIsAlteredButTheRestOfTheRowStaysIntact`, `RehydrateFailsClosedWhenOnlyIssuedAtUtcIsAlteredButTheRestOfTheRowStaysIntact`, e, sob SQL Server real, `GetByIdFailsClosedWhenOnlyTheOpaqueTokenColumnIsTamperedDirectlyInTheRow`, `GetByIdFailsClosedWhenOnlyTheProducingExecutionIdColumnIsTamperedDirectlyInTheRow`, `GetByIdFailsClosedWhenOnlyIssuedAtUtcColumnIsTamperedDirectlyInTheRow`, `TamperingIssuedAtUtcOfAnOlderWatermarkNeverSilentlyPromotesItToLatestCanonical`. |
| Checkpoint avançado/duplicado sob crash entre a emissão do token e o commit (req 6/14) | `IEvDeltaRunStore.AppendAttemptAsync` persiste a tentativa `Completed` e o watermark que ela emite NA MESMA transação SQL (`SqlEvDeltaRunStore`) — o watermark nunca se torna canônico sem a tentativa também estar commitada, e vice-versa; um crash entre a chamada ao adapter e este commit nunca deixa o par inconsistente. Comprovado sob SQL Server real por `ACompletedAttemptPersistsItsWatermarkInTheSameTransactionAndItBecomesTheCanonicalOne`. |
| Concorrência/duplicidade de efeito lógico entre execuções de fase | Idempotência é por identidade CANÔNICA (`EvDeltaRunIdentity`, hash determinístico de tenant/projeto/connector/archive/fase/watermark-anterior — deliberadamente SEM a strategy, decisão derivada) — nunca um token do cliente; o backstop SQL `UX_ev_delta_attempts_number UNIQUE (tenant_id, project_id, canonical_idempotency_key, attempt_number)`, combinado com a resolução de `run_id`/`attempt_number` sob `UPDLOCK, HOLDLOCK` na MESMA leitura, garante que tentativas concorrentes sob a MESMA chave convergem para o MESMO `run_id`, sem duplicar linha. Comprovado sob SQL Server real (8 gravações concorrentes) por `ConcurrentAppendsUnderTheSameFreshIdempotencyKeyConvergeToOneWinningAttempt`. |
| Autorização de freeze sem role competente ou implícita | `EvFreezePlan.AuthorizeFreeze` recusa fail-closed (`EvFreezeAuthorizationRequiredException`) qualquer autorização com role `Unspecified` — nunca inferido do request; a transição em si só é permitida a partir de `FreezeRequired` (`EvFreezeTransitions`, allow-list explícita). Comprovado por `AuthorizingWithUnspecifiedRoleIsAlwaysRejected`, `AuthorizingWithUnspecifiedRoleIsRejectedByTheUseCase`. |
| Freeze/final-delta/descomissionamento fora de ordem ou sem precondição | `EvFreezeTransitions.EnsureCanTransition` é uma allow-list fail-closed: tudo que não estiver explicitamente listado é recusado (ex.: `FinalDeltaReady` sem `FreezeAuthorized` persistido lança `InvalidEvFreezeTransitionException`/`EvFreezeAuthorizationRequiredException`; `DecommissionBlocked` não tem NENHUMA transição de saída). `RequestEvDeltaUseCase` exige `FreezeAuthorized` persistido ANTES de qualquer chamada ao adapter para `FinalDelta` — nunca aciona uma ação real. Comprovado por `FinalDeltaReadyRequiresAPersistedAuthorization`, `FinalDeltaWithoutAnAuthorizedFreezeIsRejected`, `DecommissionBlockedHasNoOutgoingTransitionsWhatsoever`. |
| Descomissionamento liberado por engano neste Passo | `AttemptDecommissionUseCase` só tem UMA saída possível: `DecommissionBlocked` — nunca uma execução real; chamado repetidamente permanece idempotentemente bloqueado. Comprovado por `DecommissionRemainsBlockedThroughTheFullHappyPath`, `FullFreezeLifecycleEndsPermanentlyBlockedAtDecommission`. |
| Alteração concorrente do plano de freeze mascarada como sucesso | `IEvFreezePlanStore.SaveAsync` exige a versão ANTERIOR à transição aplicada em memória; um `UPDATE` que afeta zero linhas (versão divergente) lança `ConcurrencyException` — nunca sobrescreve silenciosamente uma autorização/decisão concorrente. Comprovado sob SQL Server real por `SavingAFreezePlanWithAStaleExpectedVersionFailsClosed`. |
| Escalação via identidade de manutenção | Nenhum store deste Passo (`SqlEvWatermarkStore`, `SqlEvDeltaRunStore`, `SqlEvFreezePlanStore`, `SqlEvDeltaAuditTrail`) abre conexão de manutenção — todos operam exclusivamente sob a identidade do TENANT já resolvido; a allowlist de `EvWorkerBoundaryTests.MaintenanceIdentityIsRestrictedToApprovedCrossTenantInfrastructureOperations` permanece com os MESMOS três arquivos dos Passos 1-2, sem adição. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos Passos 1-2. |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Freeze real de ingestão/shortcut no Enterprise Vault, alteração de policy/retention ou acesso de usuário no
EV, descomissionamento/deleção de EV/Vault Store/archive, execução contra ambiente EV real de cliente sem
support-matrix certificada e host explicitamente autorizado, estratégia genérica baseada somente em
`ReceivedDate`, Outlook/COM automation, AzCopy/Azure staging/SAS, Purview/Graph/Exchange Online/import job,
reconciliação M365, avanço para I5. Nenhum destes fluxos existe no código deste Passo — não há superfície
de ameaça nova a analisar para eles aqui.
