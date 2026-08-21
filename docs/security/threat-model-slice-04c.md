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
