# Threat model — I5/EPIC-06, Passo 1 (Purview Capability Registry & Prechecks)

Delta sobre o modelo de ameaças da plataforma (mesmo formato dos deltas de Slice 4C, incorporados abaixo do
capítulo anterior em [`threat-model-slice-04c.md`](threat-model-slice-04c.md)). Escopo: capability
discovery/evidence do adapter Purview Network Upload (runbook §24, ADR-0006/0007), tenant/mailbox precheck
read-only e o policy/capacity gate de archive import (runbook §25.2-§25.4) — **sem** coleta/armazenamento de
SAS, sem AzCopy, sem staging/upload real, sem geração/validação de mapping CSV, sem criação/início de
Purview import job, sem Graph writes, sem `Enable-Mailbox -Archive`, sem habilitar auto-expanding archive,
sem alteração de role group/PIM/Conditional Access, sem Exchange Online write operations e sem
reconciliação/post-import (STOP-THE-LINE de
[`docs/engineering/requests/AB-I5-001.md`](../engineering/requests/AB-I5-001.md)). Nenhum destes fluxos
existe no código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.

## Ativos adicionais

- **Capability evidence** (`purview_capability_evidence`): rota de capability, nível de suporte
  (`GeneralAvailability`/`Preview`/`Contractual`/`Unsupported`/`Unknown`), fonte oficial (citação
  ADR/documentação — nunca URL de tenant), versão de documentação/capability quando disponível e data
  observada. Evidência de GOVERNANÇA/decisão arquitetural, não conteúdo de mailbox nem segredo.
- **Snapshots de precheck de mailbox** (`purview_mailbox_prechecks`): identidade resolvida do archive de
  destino, `ExchangeGuid`/`ArchiveGuid`, status do Online Archive, tipo de destinatário, sinalizadores de
  auto-expansion/holds e estatísticas de capacidade em bytes estruturados. Evidência OPERACIONAL read-only —
  **nunca** assunto/corpo/remetente/destinatário/anexo ou qualquer conteúdo de mailbox.

## Classificação de dados

As DUAS tabelas novas (`purview_capability_evidence`, `purview_mailbox_prechecks`) **não são "zero PII"**:
a identidade de mailbox (UPN) e os GUIDs de Exchange/archive são metadados operacionais atribuíveis a uma
mailbox específica, mesma classificação das tabelas de inventário EV (Slice 3/4C). O que elas **não**
contêm: SAS, AzCopy, credencial, token, transcript PowerShell bruto, ou qualquer campo de conteúdo de
mailbox — `CapabilityEvidence`/`MailboxPrecheckSnapshot` (Domain) têm construtores fechados (`Record`/
`Observe`) que só aceitam os campos normalizados documentados aqui, tornando estruturalmente impossível
persistir um campo de conteúdo por engano (mesma garantia já usada em `InventoryArchiveRecord`/
`EvExportManifestEntry`).

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| Capability desconhecida/não documentada tratada como suportada | `CapabilityEvidencePolicy.EnsureGeneralAvailability` nunca infere: `Unknown` bloqueia sempre; nenhuma rota fora de `PurviewCapabilityCatalog` (matriz embarcada, espelha ADR-0006/0007) é tratada como GA — "honestidade comercial", mesma regra de `ConnectorSupportMatrix`/ADR-0013. Comprovado por `UnknownRouteIsNeverInferredAsSupported`, `UnknownStatusBlocks`. |
| Capability Preview/Contractual promovida implicitamente a GA | `CapabilityEvidencePolicy` só devolve `Usable` quando `Status == GeneralAvailability`; Preview/Contractual sempre devolvem `NotGeneralAvailability`, que `PurviewPrecheckGate` trata como bloqueio antes de qualquer outra checagem. Comprovado por `PreviewOrContractualIsNeverTreatedAsGa`, `NonUsableCapabilityBlocksBeforeAnyOtherCheck`. |
| Capability evidence stale/adulterada aceita como vigente | `CapabilityEvidencePolicy` avalia staleness contra `RecordedAtUtc` (não `ObservedAtUtc`, que nunca "renova" sozinho) e bloqueia (`Stale`) acima da janela de frescor; a persistência é fronteira NÃO CONFIÁVEL — `CapabilityEvidence.Rehydrate` recomputa `EvidenceHash` a partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência (mesmo princípio de `EvWatermark`/`InventorySnapshot`). Comprovado por `EvidenceOlderThanMaxAgeSinceLastRecordedIsStale`, `RehydrateFailsClosedWhenStatusIsTamperedButHashStaysStale` e, sob SQL Server real, `GetLatestFailsClosedWhenThePersistedEvidenceHashIsTamperedDirectlyInTheRow`. |
| Downgrade/contradição de capability mascarado por uma evidência antiga "melhor" | A política SEMPRE consulta apenas a evidência de MAIOR `Version` (mais recente) — nunca a de status mais alto historicamente; um downgrade real (nova evidência com status inferior) prevalece imediatamente. Comprovado por `DowngradeToLatestEvidenceAlwaysWinsOverAnOlderHigherStatus`. |
| Archive inativo/status não determinado liberado para import | `PurviewPrecheckGate.EvaluateArchiveImport` exige `ArchiveStatus == Active`; `Unknown`/`None`/`Disabled` bloqueiam igualmente (`Unknown` nunca é tratado como `Active` — default fail-closed do enum). Comprovado por `InactiveArchiveBlocks`. |
| Auto-expanding archive usado para elevar o limite principal do adapter (100 GB) | `PurviewPrecheckGate` nunca consulta `AutoExpandingArchiveEnabled` na checagem do limite principal (`MainArchiveImportLimitBytes`, reaproveitado de `CapacityRule.OneHundredGigabytesInBytes` — mesma constante já usada pelo gate de capacidade por onda, Slice 2) — o limite é fixo independentemente do flag. Comprovado por `PlannedBytesOverMainArchiveLimitBlocksRegardlessOfAutoExpansion` (`autoExpandingArchiveEnabled: true` e `false` produzem o MESMO bloqueio). |
| Root de destino `"/"` aceito no caminho de import | Estruturalmente impossível: `ArchiveBridge.Domain.Waves.TargetRootFolder` (reutilizado sem duplicação) rejeita `"/"` no próprio construtor — uma onda aprovada nunca alcança o precheck gate com pasta raiz inválida. Comprovado (invariante já existente, reafirmado neste Passo) por `RootTargetFolderIsRejectedByTheReusedWavesValueObject`. |
| CSV acima de 500 linhas ou parte acima do limite duro liberados | `PurviewPolicyLimits` reaproveita, sem duplicar, `MappingSchema.MaxDataRows` (500) e `PartitionPolicy.RunbookHardPartBytes` (20 GB) — mudar qualquer um desses módulos muda o gate automaticamente. Comprovado por `ExactlyFiveHundredCsvRowsIsAllowed`/`FiveHundredAndOneCsvRowsIsBlocked` e `PartExactlyAtHardLimitIsAllowed`/`PartOneByteAboveHardLimitIsBlocked`. |
| Capacidade observada insuficiente liberada por ausência de margem de segurança | `PurviewPrecheckGate` exige `ObservedAvailableBytes` presente (bloqueia fail-closed com `CapacityNotObserved` quando ausente) e recusa qualquer volume planejado acima de `disponível − SafetyMarginBytes`. Comprovado por `CapacityNotObservedBlocksFailClosed`, `PlannedBytesExceedingCapacityMarginIsBlocked`. |
| Parsing locale-dependent de string formatada mascarando o volume real | Todo valor de capacidade/tamanho no gate é `long` estruturado em bytes (nunca uma string formatada pelo PowerShell parseada por regex) — mesma convenção de `PartitionPolicy`/`CapacityRule`. Nenhum tipo do Domain deste Passo aceita ou expõe um campo de texto formatado como fonte de bytes. |
| Precheck executado sobre identidade de mailbox não resolvida (IDOR) | `MailboxPrecheckSnapshot.Observe` recusa fail-closed (`PurviewValidationException`) qualquer `ArchiveRef` cuja identidade não tenha sido resolvida server-side por um manifesto/resolvedor autorizado (`IsIdentityResolved == false`) — mesmo invariante já aplicado ao gate de capacidade por onda (Slice 2, `WaveSelection.HasUnresolvedArchive`). Comprovado por `ObserveRejectsUnresolvedMailboxIdentity`. |
| Caller fabrica uma `ArchiveRef(mailbox, TargetArchiveId)` arbitrária "marcada como resolvida" para obter precheck de mailbox fora do seu escopo (IDOR — AB-I5-003) | `ArchiveRef` é um construtor público — `IsIdentityResolved == true` prova apenas a FORMA do objeto, nunca a autorização/proveniência da mailbox. `SubmitMailboxPrecheckRequest` (Application) NÃO carrega mais uma `ArchiveRef`: carrega somente `WaveId` + `TargetArchiveId` (identificadores opacos). `SubmitMailboxPrecheckUseCase` resolve a `ArchiveRef` CANÔNICA a partir de `IWaveStore.GetAsync(scope, waveId)` (mesma fonte server-side já autorizada consumida por `EvaluatePurviewPrecheckUseCase`) e só sonda o adapter com a instância encontrada na seleção da onda persistida — nunca uma reconstruída a partir de campos do caller. Onda inexistente/fora do tenant-projeto, archive fora da seleção da onda e archive presente mas ainda não resolvido produzem TODOS o MESMO `PurviewArchiveNotFoundException`, sem sondar o adapter e sem vazar existência/UPN/GUID/detalhes cross-tenant/project. Comprovado por `SubmitFailsClosedWhenTheWaveDoesNotExistInScope`, `SubmitFailsClosedWhenTheArchiveIsNotPartOfTheWaveSelection`, `SubmitFailsClosedWhenTheArchiveInTheWaveIsStillUnresolved`, `SubmitFailsClosedWhenTheWaveBelongsToAnotherTenantOrProject` (Application, com asserção de que o adapter nunca é sondado) e, sob SQL/RLS real, `SubmitFailsClosedWhenTheWaveBelongsToAnotherTenantOrProject`/`SubmitFailsClosedWhenTheArchiveIsNotPartOfTheWaveSelection` (Integration). `SubmitPersistsTheObservedSnapshotUsingTheCanonicalMailboxFromTheWave`/`SubmitPersistsTheObservedPrecheckSnapshot` provam que a mailbox de exibição sondada/persistida é sempre a canônica da onda, nunca algo injetável pelo request. |
| Vazamento cross-tenant/cross-project de capability evidence ou precheck (IDOR) | `ICapabilityEvidenceStore.GetLatestAsync`/`IMailboxPrecheckStore.GetLatestAsync` participam de `rls.tenant_isolation_policy` (FILTER + BLOCK, reforçado nesta migração para as duas tabelas novas) e filtram `project_id` explicitamente; um registro de outro tenant/projeto é indistinguível de inexistente. Comprovado sob SQL Server real por `CapabilityEvidenceFromAnotherProjectIsIndistinguishableFromNotFound`, `PrecheckFromAnotherProjectIsIndistinguishableFromNotFound`. |
| Corrida de descoberta/precheck concorrente duplicando evidência ou perdendo uma mudança real | Idempotência é por convergência de versão (mesmo padrão de `SqlConnectorInventoryStore`/`ev_connector_inventory_snapshots`, AB-4C-002): o índice único `(tenant_id, project_id, provider, route_key, version)` (evidence) / `(tenant_id, project_id, archive_identity, version)` (precheck) é o backstop SQL; uma colisão só converge (`Created=false`) quando o CONTEÚDO já persistido é igual ao candidate (`IsSameContentAs`, que deliberadamente exclui `RecordedAtUtc`/`Version`/`Id`/`Correlation` — campos que mudam a cada submissão mesmo sem mudança real); conteúdo diferente sinaliza `ConcurrencyException` e a Application releé o latest e tenta a próxima versão livre (nunca perde a mudança). Comprovado por `DiscoverConvergesUnderConcurrentIdenticalContentInsteadOfDuplicating`, `RepeatedDiscoveryWithNoRealChangeDoesNotCreateANewVersion`, `RepeatedSubmissionWithNoRealChangeDoesNotCreateANewVersion`. |
| Evidência de precheck persistida adulterada (holds/status/GUIDs/capacidade) lida como canônica | Mesma fronteira NÃO CONFIÁVEL de `EvWatermark`/`InventorySnapshot`: `MailboxPrecheckSnapshot.Rehydrate` recomputa `SnapshotHash` a partir de TODOS os campos REALMENTE carregados e recusa fail-closed (`MailboxPrecheckIntegrityViolationException`) qualquer divergência — inclui adulteração isolada de `ArchiveStatus` ou `ObservedAvailableBytes`. Comprovado sob SQL Server real corrompendo a linha por fora da aplicação: `GetLatestFailsClosedWhenTheArchiveStatusColumnIsTamperedDirectlyInTheRow`, `GetLatestFailsClosedWhenObservedAvailableBytesIsTamperedDirectlyInTheRow`. |
| Mutação de tenant/mailbox executada implicitamente pelo precheck | Nenhum tipo/porta deste Passo expõe uma operação de escrita contra Graph/EXO/PowerShell — `IMailboxPrecheckAdapter.ObserveAsync` é somente leitura por contrato (mesmo desenho de `IEvInventoryAdapter`, Slice 4C Passo 1); este Passo não inclui nenhuma implementação real de `Enable-Mailbox -Archive`/auto-expansion/role/PIM/Conditional Access — apenas o contrato e um adapter de teste/fixture determinístico. |
| Escalação via identidade de manutenção | Nenhum store deste Passo (`SqlCapabilityEvidenceStore`, `SqlMailboxPrecheckStore`) abre conexão de manutenção — ambos operam exclusivamente sob a identidade do TENANT já resolvida; a allowlist de `EvWorkerBoundaryTests.MaintenanceIdentityIsRestrictedToApprovedCrossTenantInfrastructureOperations` permanece com os MESMOS arquivos já existentes, sem adição. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos módulos anteriores. |
| Dependência vazando de Domain/Application para Graph/EXO/PowerShell/Purview SDK | Nenhum pacote/assembly de vendor é referenciado por `ArchiveBridge.Domain`/`ArchiveBridge.Application`/`ArchiveBridge.Contracts` deste módulo — `PurviewCapabilityCatalog` é uma matriz embarcada pura (mesmo padrão de `ConnectorSupportMatrix`), sem chamada em tempo real ao fornecedor. Verificado pelos testes já existentes de `VendorBoundaryTests`/`DependencyRuleTests` (sem necessidade de nova allowlist — nenhum token de vendor novo aparece no código deste Passo). |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Coleta/armazenamento de SAS, Key Vault para SAS, execução de AzCopy, staging/upload real, geração/validação
de mapping CSV, criação/validação/início de Purview import job, Graph writes, `Enable-Mailbox -Archive`,
habilitar auto-expanding archive, criar/alterar role group/PIM/Conditional Access, Exchange Online write
operations, reconciliação/post-import, avanço para I6. Nenhum destes fluxos existe no código deste Passo —
não há superfície de ameaça nova a analisar para eles aqui.

# Threat model — I5/EPIC-06, Passo 2 (Secure SAS Intake & Custody)

Delta sobre o capítulo anterior (mesmo formato). Escopo: entrada segura, validação fail-closed, custódia
temporária (DPAPI, perfil nó único) e ciclo de vida do SAS do Network Upload (runbook §25.5-§25.6,
ADR-0006/0008), preparando o contrato para o futuro upload worker — **sem** AzCopy/processo externo, sem
upload/staging real, sem Azure Key Vault/Managed Identity obrigatória, sem habilitar o perfil HA de
segredos, sem mapping CSV, sem Purview import job, sem Graph/EXO writes e sem reconciliação
(STOP-THE-LINE de [`docs/engineering/requests/AB-I5-004.md`](../engineering/requests/AB-I5-004.md)).

## Ativos adicionais

- **Handle de custódia do SAS** (`purview_sas_upload_handles`): metadado OPACO — estado do ciclo de vida,
  fingerprint SHA-256 não reversível do segredo completo, referência opaca ao secret store, host/container
  canonicalizados (metadados NÃO secretos), expiry, geração/versão, linkage de auditoria. Evidência de
  GOVERNANÇA/custódia, nunca o segredo em si — `PurviewSasUploadHandle` tem construtores fechados
  (`Intake`/`Rehydrate`) que só aceitam os campos normalizados documentados aqui, tornando estruturalmente
  impossível persistir a query/assinatura SAS por engano (mesma garantia de `CapabilityEvidence`/
  `MailboxPrecheckSnapshot`, Passo 1).
- **Material protegido do secret store** (`purview_sas_secret_material`): ciphertext DPAPI + entropia do
  SAS completo. Segredo em repouso, protegido por `ProtectedData.Protect` sob a identidade Windows dedicada
  do workload (`DataProtectionScope.CurrentUser`) — ilegível sem essa identidade específica, mesmo com
  acesso de leitura ao SQL Server. Nenhuma identidade de MANUTENÇÃO recebe qualquer `GRANT` nesta tabela.

## Classificação de dados

A URL SAS bruta (com query/assinatura) é **segredo** — nunca "zero PII", nunca metadado operacional: dá
acesso de escrita ao staging temporário do Purview. Ela NUNCA é persistida em texto claro/estrutura
reversível em SQL, log, trace, exceção, telemetria, evidence payload ou resposta ao chamador — apenas o
ciphertext DPAPI (ilegível sem a identidade dedicada) chega ao SQL, e apenas o fingerprint não reversível
(SHA-256 do valor completo) e metadados canonicalizados não secretos (host, container, expiry, permissões
estruturadas) chegam ao handle de auditoria. `RedactedSecret` (Domain.Common) é o único tipo que carrega o
valor bruto em trânsito — sem `ToString`, sem propriedade pública de dados, sem igualdade estrutural — e é
usado em toda a superfície de intake/custódia (`IntakePurviewSasRequest`, `ISecretStore`).

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| SAS ecoado/persistido em texto claro (SQL, log, exceção, telemetria, problem details, response) | `RedactedSecret` (Domain.Common) é o único portador do valor bruto em trânsito: sem `ToString` que o imprima, sem propriedade/campo público de dados (um serializador automático produz `{}`), sem igualdade estrutural. `PurviewSasUploadHandle` só persiste fingerprint SHA-256 não reversível + metadados canonicalizados NÃO secretos — nenhum construtor aceita a query/assinatura. Comprovado por `RedactedSecretHasNoPublicDataMemberAndSerializesToAnEmptyObject`, `RedactedSecretToStringNeverPrintsTheValue`, `RedactedSecretInterpolatedIntoAnExceptionMessageNeverLeaksTheValue` (canary), `RejectedValidationResultNeverCarriesTheSecretOrAnyNonSecretMetadata` e, por reflexão estrutural, `RedactedSecretExposesNoPublicDataMember`. |
| Host arbitrário aceito por heurística de string (`Contains`) em vez de validação estrutural | `PurviewSasIntakePolicy` valida `Uri.Host` (já parseado pelo BCL) por SUFIXO exato (`.blob.core.windows.net`) com exigência de um label não vazio antes do sufixo — nunca `Contains` sobre a string bruta. Comprovado por `HostOutsideAuthorizedSuffixIsRejected` (inclui o caso `blob.core.windows.net.attacker.com`, onde o sufixo aparece no MEIO da string, e `notblob.core.windows.net`, sem o ponto separador) e `HostWithAValidStorageAccountLabelBeforeTheSuffixIsAccepted`. |
| Container/path diferente de `ingestiondata` aceito (runbook §25.5/§25.7) | `PurviewSasIntakePolicy` exige exatamente UM segmento de path igual a `ingestiondata`, case-sensitive — qualquer segmento extra, ausência ou diferença de case é recusado. Comprovado por `ContainerDifferentFromIngestiondataIsRejected`, `ContainerCaseDifferenceIsRejected`, `ExtraPathSegmentAfterContainerIsRejected`. |
| Scheme alternativo, userinfo ou fragment escondendo um destino diferente do pretendido | `PurviewSasIntakePolicy` recusa fail-closed qualquer esquema != `https`, qualquer `UserInfo` não vazio e qualquer `Fragment` não vazio — antes de qualquer outra checagem. Comprovado por `HttpSchemeIsRejected`, `UserInfoInUrlIsRejected`, `FragmentInUrlIsRejected`. |
| Parâmetro crítico do SAS (`sv`/`se`/`sp`/`sig`) ausente, duplicado ou ambíguo aceito | O parser de query string NUNCA usa `HttpUtility`/`NameValueCollection` (que colapsariam silenciosamente chaves duplicadas) — detecta qualquer chave crítica repetida e recusa o parsing inteiro; ausência de `sv`/`sig`/`se`/`sp` recusa fail-closed. SAS por policy nomeada (`si`) — cujas permissões/expiry não são verificáveis estaticamente — é recusado, nunca presumido "provavelmente ok". Comprovado por `DuplicateCriticalParameterIsRejected`, `MissingSignedVersionIsRejected`, `MissingSignatureIsRejected`, `StoredPolicyIdentifierReferenceIsRejected`. |
| Expiry ausente, malformado, já vencido, vencendo "logo demais" ou distante demais aceito | `se` é parseado estruturadamente (`DateTimeOffset`, nunca regex sobre string) e exigido presente/válido; a policy própria do produto (não documentada pela Microsoft) exige margem mínima futura E limita a janela máxima de validade — defesa em profundidade contra um SAS de vida útil excessiva. Comprovado por `MalformedExpiryIsRejected`, `AlreadyExpiredIsRejected`, `ExpiryWithinMinimumMarginIsRejected`, `ExpiryBeyondMaximumWindowIsRejected`, `ExpiryExactlyAtMaximumWindowIsAccepted`. |
| Permissões mais amplas que o necessário para upload (delete/list/immutability/ownership) aceitas | `PurviewSasPermissions.SatisfiesUploadPolicy` exige Create+Write E recusa qualquer permissão de controle administrativo do container; letra de permissão não reconhecida recusa o parsing inteiro (nunca ignorada). Comprovado por `PermissionsOutsideUploadPolicyAreRejected`, `UnrecognizedPermissionLetterIsRejected`. |
| SAS custodiado lido em texto claro por qualquer caminho além do boundary do upload worker | `ISecretStore.AcquireAsync` é a ÚNICA operação de leitura em texto claro de toda a porta, e é chamada em EXATAMENTE um lugar de toda a Application (`AcquireSasForUploadUseCase`) — verificado estruturalmente. A identidade requerente (`WorkloadIdentity`) é revalidada em DUAS camadas independentes: a Application (antes de chamar o adapter) E o próprio `DpapiSecretStore` (defesa em profundidade — um chamador futuro que invoque `ISecretStore` fora do caso de uso não contorna o boundary). Nenhum arquivo do ControlPlane referencia o caso de uso ou a porta (guarda de regressão hoje vale por vacuidade — nenhuma superfície HTTP existe ainda neste Passo). Comprovado por `SecretAcquireAsyncIsCalledFromExactlyOnePlaceInTheApplication`, `NoControlPlaneSourceFileReferencesTheAcquisitionUseCaseOrTheSecretStorePort`, `AcquireByAnUnauthorizedIdentityIsDeniedAndNeverTouchesTheSecretStore`, `DpapiSecretStoreDeniesAcquisitionByAnUnauthorizedIdentityEvenWhenSupported`. |
| Reuso do SAS além de uma aquisição, ou aquisição concorrente vazando o segredo para o perdedor de uma corrida | **Revisado por AB-I5-006 item 2** (ver delta abaixo) — `AcquireSasForUploadUseCase` reivindica `Available -> Claimed` por concorrência otimista (`row_version`) E sob fencing por época (`ClaimEpoch`, mesmo padrão de `Job`/`LeaseEpoch`) ANTES de chamar `ISecretStore.AcquireAsync`; nenhum chamador que perca a corrida da reivindicação chega a ver o segredo. Comprovado por `TwoConcurrentClaimAttemptsNeverBothReceiveTheSecret`, `ASecondAcquireAttemptAfterConsumptionIsDenied` e, sob SQL real, `ClaimWithAStaleRowVersionFailsClosed`. |
| Handle expirado ou destruído readquirido | `AcquireSasForUploadUseCase` avalia expiry no momento da aquisição (marca `Expired` explicitamente antes de recusar) e só aceita estado `Available`; `PurviewSasUploadHandle.MarkAvailable` só é alcançável a partir de `Stored` — `Expired`/`Destroyed` NUNCA retornam a `Available` sem um novo intake explícito (nova geração). Comprovado por `AcquireAfterExpiryIsDeniedAndMarksTheHandleExpired`, `ExpiredAndDestroyedNeverTransitionBackToAvailable`. |
| Corrida de intake concorrente para a mesma wave produzindo dois handles "vivos" simultâneos | O índice único FILTRADO `UX_psuh_canonical_live` (estados Stored/Available/Consumed) é o backstop SQL — a perdedora de uma corrida de PRIMEIRO intake recebe `ConcurrencyException` e a Application releé o canônico e converge para a próxima geração; um replace com `expectedPrevious` obsoleto (row_version divergente) também recusa fail-closed. Comprovado sob SQL Server real por `ConcurrentFirstIntakeForTheSameWaveNeverProducesTwoLiveCanonicalHandles`, `ReplacingWithAStaleExpectedPreviousFailsClosed`. |
| Novo SAS para a mesma wave reaproveitando/mascarando o handle anterior sem trilha auditável | Um novo intake cria uma NOVA geração (nunca sobrescreve a linha existente) e marca a geração anterior `Destroyed` na MESMA transação atômica — histórico completo preservado, nunca perdido. Comprovado por `ANewIntakeForTheSameWaveVersionsAndDestroysThePreviousGeneration` e, sob SQL real, `ReplacingDestroysThePreviousGenerationAndInsertsTheNewOneAtomically`. |
| Handle de um tenant/projeto/wave usado/lido em outro (IDOR/replay cross-scope) | `IPurviewSasUploadHandleStore` participa de `rls.tenant_isolation_policy` (FILTER + BLOCK, reforçado nesta migração) e filtra `project_id`/`wave_id` explicitamente; um handle de outro tenant/projeto é indistinguível de inexistente — a wave em si é resolvida via `IWaveStore` (mesma fonte server-side já autorizada dos Passos anteriores), nunca um `WaveId` aceito sem prova de pertencimento ao escopo. Comprovado por `IntakeFromOneTenantIsNeverVisibleAsCanonicalToAnotherTenant` e, sob SQL Server real, `HandleFromAnotherProjectIsIndistinguishableFromNotFound`. |
| Handle de custódia persistido adulterado (estado/fingerprint/expiry/referência) lido como canônico | Mesma fronteira NÃO CONFIÁVEL de `CapabilityEvidence`/`MailboxPrecheckSnapshot`: `PurviewSasUploadHandle.Rehydrate` recomputa `HandleHash` a partir de TODOS os campos REALMENTE carregados e recusa fail-closed (`PurviewSasHandleIntegrityViolationException`) qualquer divergência. Comprovado por `RehydrateFailsClosedWhenHandleHashDoesNotMatchLoadedFields` e, sob SQL Server real corrompendo a linha por fora da aplicação, `GetCanonicalFailsClosedWhenTheHandleHashIsTamperedDirectlyInTheRow`. |
| Mecanismo de segredo indisponível (host não-Windows) mascarado como sucesso ou com fallback inseguro | `DpapiSecretStore` verifica `OperatingSystem.IsWindows()` ANTES de qualquer chamada real à API — indisponibilidade lança `SecretStoreUnavailableException` (fail-closed), nunca um fallback para texto claro ou mecanismo alternativo não certificado (ADR-0008: perfil HA de segredos permanece `BLOCKED_PENDING_EVIDENCE`, nenhuma pseudo-HA). Comprovado sob o runner de CI deste repositório (Ubuntu — não-Windows) por `DpapiSecretStoreRoundTripsWhenSupportedAndFailsSafeOtherwise`, que exercita EXATAMENTE este ramo em cada execução do pipeline. |
| Destruição local do material apresentada como revogação remota do SAS no Purview/Microsoft Storage | `DestroySasHandleUseCase`/`ISecretStore.DestroyAsync` documentam explicitamente que a operação é LOCAL — nenhum tipo/porta deste Passo chama qualquer API do Purview/Azure Storage para revogar o SAS remotamente (nenhum SDK de fornecedor é referenciado, ver abaixo). A validade/revogação remota permanece sob controle exclusivo da Microsoft (ADR-0006). |
| Destruição local não idempotente causando erro em reprocessamento/retry operacional, ou material órfão quando o processo cai ENTRE a transição de metadado e a remoção do material | **Revisado por AB-I5-006 item 3** (ver delta abaixo) — `PurviewSasUploadHandle.Destroy` é idempotente por desenho; `DestroySasHandleUseCase` transiciona o metadado para `Destroyed` PRIMEIRO (fencing/inacessibilidade durável) e SÓ DEPOIS remove o material — uma queda entre as duas etapas nunca deixa o metadado "aparentando disponível" apontando para material já removido, e uma nova chamada RETOMA e reexecuta a remoção do material (idempotente por si só em `ISecretStore.DestroyAsync`) mesmo quando o metadado já estava `Destroyed`. Comprovado por `DestroyIsIdempotentFromAnyState` (Domain) e `DestroyIsIdempotentInResultButAlwaysRetriesTheSecretStoreDestroyForCrashSafety`, `WhenTheSecretStoreDestroyFailsTheMetadataIsAlreadyDestroyedAndARetryConverges` (Application). |
| Dependência vazando de Domain/Application/Contracts para DPAPI/Windows/Azure Key Vault | Nenhum arquivo-fonte de Domain/Application/Contracts referencia `ProtectedData`/`DataProtectionScope`/o assembly do pacote DPAPI — só `ArchiveBridge.Infrastructure` (`DpapiSecretStore`) o faz, sob `[SupportedOSPlatform("windows")]`. Comprovado por `DomainApplicationAndContractsSourceNeverReferenceDpapiTypes`, `DomainContractsAndApplicationAssembliesDoNotReferenceTheDpapiPackageAssembly` — sem necessidade de nova allowlist em `VendorBoundaryTests`, pois DPAPI não é um vendor de fornecedor externo (é uma API do próprio Windows), mas o mesmo princípio de isolamento se aplica. |
| Identidade de manutenção usada para ler/persistir o handle ou o material protegido | Nenhum store deste Passo (`SqlPurviewSasUploadHandleStore`, `DpapiSecretStore`) abre conexão de manutenção — ambos operam exclusivamente sob a identidade do TENANT já resolvida; `purview_sas_secret_material` não concede NENHUM `GRANT` à identidade de manutenção (mais restritivo que o padrão append-only já usado nas demais tabelas). Comprovado sob SQL Server real por `Migration0027AppliesCleanlyAndPriorHashesRemainStable` (asserção de zero `GRANT`s). |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos módulos anteriores. |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Execução de `azcopy.exe`/qualquer processo externo, SAS em command line, upload/staging real no container
Microsoft, Azure Key Vault/Managed Identity como baseline obrigatória, habilitação do perfil HA/multi-node
de segredos, geração/validação de mapping CSV, criação/validação/início de Purview import job, Graph
writes/Exchange Online writes, reconciliação/post-import, logar/transmitir/copiar o SAS para ticket/e-mail/
documento/transcript, avanço para o próximo Passo dentro deste PR. Nenhum destes fluxos existe no código
deste Passo — não há superfície de ameaça nova a analisar para eles aqui.

# Threat model — I5/EPIC-06, Passo 2 (revisão AB-I5-006: claim/lease/fencing e crash-consistency)

Delta sobre o capítulo imediatamente anterior (mesmo Passo, mesmo PR). `AB-I5-005` identificou três
blockers na revisão de `AB-I5-004`; `AB-I5-006` resolveu o item 1 por decisão de engenharia (documentada
abaixo) e determinou a implementação integral dos itens 2 e 3. Nenhum STOP-THE-LINE de `AB-I5-004` foi
alterado; nenhum AzCopy/processo externo/upload real/reconciliação foi introduzido.

## Decisão de engenharia — validação de host permanece estrutural (item 1)

`AB-I5-005` apontou que `PurviewSasIntakePolicy` aceita qualquer host sob o sufixo `.blob.core.windows.net`
por validação estrutural, sem confirmar que aquele storage account específico é o destino Purview
verdadeiramente autorizado para a wave. A investigação (`CLAUDE_BLOCKED` sobre `AB-I5-005`) confirmou que
isso é estruturalmente impossível de fechar neste Passo: o storage account de destino do Network Upload é
provisionado DINAMICAMENTE pela própria Microsoft a cada novo import job criado manualmente no portal
Purview (runbook §25.5: "o operador cria um novo import job, seleciona upload e copia a URL SAS"; ADR-0006:
"a criação e o início do import job permanecem como tarefa de workflow humana no portal Purview") — não
existe hoje, em nenhuma fonte de autoridade do repositório, um conceito de "storage account pré-autorizado
por tenant/projeto/wave" que o ArchiveBridge possa consultar ANTES do instante em que o operador cola a
URL SAS. Inventar esse conceito agora seria uma decisão de produto fora do escopo autorizado (quem
registra a autorização, em que granularidade, como sobrevive à rotação por import job) — um mecanismo mal
desenhado teria efeito de segurança PIOR que nenhum (aceitaria facilmente um host arbitrário do atacante
"pré-autorizado", ou bloquearia falsamente todo intake legítimo).

**Decisão**: a validação de host permanece a checagem estrutural fail-closed já implementada (sufixo
`.blob.core.windows.net` + label não vazio + container/path `ingestiondata` exato, case-sensitive) — o
limite do que é comprovável sem I/O externo neste Passo. Isto NÃO é tratado como prova de que o storage
account pertence ao import job Purview esperado — é registrado explicitamente aqui como uma limitação
aceita. Como este Passo não executa AzCopy/upload real (STOP-THE-LINE), não há exfiltração possível neste
boundary hoje. **Antes de habilitar upload real em um Passo posterior**, o work order daquele Passo deve
fechar este trust boundary com a evidência disponível naquele estágio (ex.: confirmação humana explícita no
momento do import job, ou uma fonte de autoridade ainda a definir) — sem inventar o mecanismo agora.

## Claim/lease/fencing de aquisição (item 2)

O desenho anterior (`AB-I5-004`) queimava a geração (`Available -> Consumed`) ANTES de chamar
`ISecretStore.AcquireAsync` — uma falha do secret store DEPOIS dessa transição deixava o handle
irreversivelmente `Consumed` sem que o upload worker tivesse recebido o segredo (perda de disponibilidade,
sem retry seguro).

**Mecanismo**: um novo estado `SasHandleState.Claimed` (adicionado ao FINAL da faixa numérica — 5 — para
nunca renumerar os estados já persistidos) representa uma reserva de uso único sob lease/fencing por época
(`PurviewSasUploadHandle.ClaimOwner`/`ClaimEpoch`/`ClaimExpiresAtUtc` — mesmo padrão já usado por
`Job`/`LeaseEpoch`, ADR-0003, e não duplicado: `LeaseEpoch` é reaproveitado diretamente do módulo Jobs).
`AcquireSasForUploadUseCase.ExecuteAsync`: (1) reivindica `Available -> Claimed` por concorrência otimista
(`row_version`) — o perdedor de uma corrida de PRIMEIRA reivindicação NUNCA chama `ISecretStore.AcquireAsync`;
um claim ativo e ainda dentro do lease de OUTRO adquirente é recusado imediatamente, sem tocar o secret
store; um claim com lease EXPIRADO é recuperável via `Reclaim` (rotaciona owner/época — o titular anterior
nunca mais finaliza com a época antiga, mesmo que retorne tarde demais); (2) SOMENTE DEPOIS lê o segredo;
(3) finaliza `Claimed -> Consumed` (`FinalizeClaim`) SOB A MESMA ÉPOCA do claim, SOMENTE após a leitura ter
tido sucesso — nunca antes. Uma falha do secret store, cancelamento ou queda de processo ENTRE o claim e a
leitura NUNCA queima a geração: o lease simplesmente expira e um novo adquirente (ou o mesmo, em retry)
recupera via `Reclaim`. O lease nunca ultrapassa a validade restante do próprio SAS.

Comprovado por `ClaimIncrementsTheEpochEachTimeItIsReivindicated`, `FinalizeClaimWithTheWrongOwnerIsRejectedByFencing`,
`FinalizeClaimWithAStaleEpochIsRejectedByFencing`, `ReclaimBeforeTheLeaseExpiresIsRejectedFailClosed`,
`ReclaimRotatesTheOwnerAndTheOldOwnerCanNeverFinalizeAgain` (Domain); `TwoConcurrentClaimAttemptsNeverBothReceiveTheSecret`,
`AcquireWhileAnotherClaimIsStillWithinItsLeaseIsDeniedWithoutTouchingTheSecretStore`,
`AFailedSecretStoreReadAfterClaimingNeverBurnsTheGenerationAndIsRecoverableByReclaimAfterTheLeaseExpires`,
`TheClaimLeaseNeverOutlivesTheSasExpiryEvenWithALongLeaseDuration` (Application); e, sob SQL Server real,
`ClaimTransitionPersistsAcrossReads`, `ReclaimAfterLeaseExpiryPersistsAndRotatesTheEpochAndOwner`,
`FinalizeClaimAfterAcquisitionPersistsAsConsumed`, `ClaimWithAStaleRowVersionFailsClosed`,
`GetCanonicalFailsClosedWhenTheClaimOwnerIsTamperedDirectlyInTheRow` (Integration).

**AB-I5-007 — entrega do segredo é fail-closed sob perda de fencing (corrigido, não mais risco residual)**:
o desenho revisado por `AB-I5-006` deixava a janela ENTRE a leitura bem-sucedida do secret store e a
persistência de `FinalizeClaim` tratada como "corrida residual best-effort" — se a finalização falhasse por
`ConcurrencyException` (porque o lease titular expirou e outro adquirente já reivindicou por `Reclaim` nesse
intervalo), a exceção era engolida e o segredo já lido era retornado ao caller antigo mesmo assim. Isso
permitia, na janela de expiração do lease, que dois adquirentes recebessem o MESMO SAS — quebrando a
garantia de uso único e o próprio propósito do fencing por época.

**Correção**: `AcquireSasForUploadUseCase.ExecuteAsync` só devolve o segredo ao caller se a transição
`Claimed -> Consumed` sob a MESMA época do claim for persistida com sucesso — a prova, no momento exato da
entrega, de que este requester ainda é o titular do claim. Se `SaveTransitionAsync(FinalizeClaim(...))`
falhar por `ConcurrencyException` (owner/época já rotacionados por `Reclaim` de outro adquirente), o método
falha fechado com `PurviewSasAcquisitionDeniedException` — o segredo já lido pelo secret store NUNCA
atravessa para o caller. Nenhuma compensação é necessária nesse ramo: nenhuma transição foi persistida por
este requester, e o claim já pertence legitimamente ao novo owner/época. A recuperação de crash/cancelamento
ANTES da leitura do segredo (lease expira, `Reclaim` recupera) permanece inalterada — apenas a entrega FINAL
do segredo, após leitura bem-sucedida, deixou de ser best-effort.

Comprovado por `AFinalizeClaimLostToAConcurrentReclaimNeverReturnsTheSecretToTheStaleClaimant`,
`OnlyTheClaimantThatPersistsConsumedReceivesTheSecret`,
`AStaleRowVersionAtFinalizeIsNeverTreatedAsASuccessfulDelivery`,
`CancellationOrFailureBeforeFinalizeRemainsRecoverableByReclaimWithoutDoubleDelivery` (Application), e sob
SQL Server real, `FinalizeClaimLostToAConcurrentReclaimFailsClosedUnderRealConcurrency` (Integration).

**AB-I5-008 — expiração temporal do lease/SAS no instante da entrega é fail-closed mesmo sem reclaim
concorrente (corrigido)**: `AB-I5-007` fechou a corrida de fencing por época, mas a correção reaproveitava o
`now` capturado ANTES do claim para validar `FinalizeClaim` DEPOIS da leitura do secret store — e
`PurviewSasUploadHandle.FinalizeClaim` validava apenas owner/época, nunca a validade temporal do lease
(`ClaimExpiresAtUtc`) nem do próprio SAS (`ExpiresAtUtc`). Consequência: se a leitura do secret store
demorasse além do lease, mas NENHUM concorrente tivesse feito `Reclaim` ainda (row_version continuava
"fresco"), o claimant original conseguia persistir `Claimed -> Consumed` com sucesso e receber o SAS DEPOIS
do lease (ou do próprio SAS) já terem expirado — contradizendo o fail-closed exigido desde `AB-I5-006`.

**Correção**: `AcquireSasForUploadUseCase.ExecuteAsync` relê `_clock.UtcNow` IMEDIATAMENTE após a leitura
bem-sucedida do secret store — nunca reaproveita o instante capturado antes do claim. `PurviewSasUploadHandle
.FinalizeClaim` agora exige, além do fencing por owner/época já existente, que `ClaimExpiresAtUtc` e
`ExpiresAtUtc` (SAS) sejam ESTRITAMENTE maiores que o instante de finalização informado — `nowUtc ==
ClaimExpiresAtUtc`/`ExpiresAtUtc` já falha fechado (boundary exclusivo). A rejeição temporal ocorre no
Domain, ANTES de qualquer tentativa de persistência: nenhuma transição é escrita no SQL, o handle permanece
`Claimed` sob o owner/época atuais (sem nenhuma compensação insegura que o reverta para `Available`) e
continua recuperável por `Reclaim` assim que um adquirente observar o lease expirado — exatamente o mesmo
caminho de recuperação já usado para falha do secret store/cancelamento ANTES da leitura.

Comprovado por `FinalizeClaimWithinTheLeaseAndSasValidityStillSucceeds`,
`FinalizeClaimExactlyAtTheClaimLeaseExpiryBoundaryFailsClosed`,
`FinalizeClaimAfterTheClaimLeaseExpiresWithoutAnyConcurrentReclaimFailsClosed`,
`FinalizeClaimExactlyAtTheSasExpiryBoundaryFailsClosedEvenWithinTheClaimLease`,
`FinalizeClaimAfterTheSasExpiresFailsClosedEvenWithinTheClaimLease`,
`FinalizeClaimBeforeTheSasExpiresAndWithinTheClaimLeaseSucceeds`,
`AHandleWithAnExpiredLeaseLeftUnfinalizedRemainsClaimedAndRecoverableByReclaim` (Domain);
`ALeaseThatExpiresDuringTheSecretStoreReadWithoutAnyConcurrentReclaimDeniesDeliveryAndNeverPersistsConsumed`,
`FinalizingExactlyAtTheClaimLeaseExpiryBoundaryFailsClosed`,
`FinalizingWithinTheClaimLeaseUsingTheReReadClockStillSucceeds`,
`ASasThatExpiresDuringTheSecretStoreReadDeniesDeliveryAndNeverPersistsConsumed`,
`ALeaseThatExpiresDuringTheReadRemainsRecoverableByReclaimAfterwards` (Application); e sob SQL Server real,
`ATemporallyExpiredFinalizeNeverPersistsConsumedEvenWithAFreshRowVersion` (Integration).

## Crash-consistency do lifecycle do secret material (item 3)

O desenho anterior tinha três lacunas: (a) `IntakePurviewSasUseCase` podia deixar o material recém-protegido
permanentemente órfão se a convergência do metadado nunca tivesse sucesso (contenção persistente) ou uma
exceção/cancelamento interrompesse o fluxo; (b) ao substituir uma geração, o material da geração ANTERIOR
nunca era destruído (vazamento de ciphertext indefinido); (c) `DestroySasHandleUseCase` apagava o material
ANTES de persistir `Destroyed` no metadado — uma queda entre as duas etapas deixava o metadado aparentando
`Available`/`Consumed` mas apontando para material já removido.

**Mecanismo** (nenhuma transação distribuída real entre `IPurviewSasUploadHandleStore` e `ISecretStore` —
são portas independentes por desenho, substituíveis separadamente; a correção usa compensação best-effort
e reordenação, não uma transação de duas fases):

- `IntakePurviewSasUseCase`: protege o segredo UMA única vez; se NENHUMA tentativa de convergência tiver
  sucesso, ou uma exceção/cancelamento interromper o fluxo ANTES de o candidato se tornar o canônico
  persistido, o material recém-protegido é destruído por compensação (nunca mascara a exceção original).
  Uma vez que o candidato SE TORNA o canônico persistido, o material da geração ANTERIOR substituída (já
  `Destroyed` no metadado, na MESMA transação atômica do insert) também é destruído por compensação.
- `DestroySasHandleUseCase`: o metadado transiciona para `Destroyed` PRIMEIRO (inacessível a
  `AcquireSasForUploadUseCase`, que só reivindica a partir de `Available`/`Claimed`) — SÓ DEPOIS o material
  é removido. `ISecretStore.DestroyAsync` é SEMPRE reexecutado (mesmo quando o metadado já estava
  `Destroyed` de uma tentativa anterior), convergindo por retry.

Comprovado por `AFailedSecretStoreReadAfterClaimingNeverBurnsTheGenerationAndIsRecoverableByReclaimAfterTheLeaseExpires`
(compensação de claim, acima); `WhenTheSecretStoreDestroyFailsTheMetadataIsAlreadyDestroyedAndARetryConverges`,
`DestroyIsIdempotentInResultButAlwaysRetriesTheSecretStoreDestroyForCrashSafety` (Application).

**Risco residual aceito**: uma compensação é, por natureza, best-effort — se o PRÓPRIO PROCESSO cair (perda
de energia, kill -9) exatamente entre a chamada de `ISecretStore.ProtectAsync`/`DestroyAsync` bem-sucedida e
a compensação/transição subsequente, o material pode permanecer órfão sem que nenhuma compensação chegue a
rodar (a mesma categoria de risco já aceita e documentada em `AB-I5-004`: "material órfão... permanece
protegido e inacessível — nunca texto claro — até uma rotina de expurgo futura"). Nenhum reconciliador/
reaper em segundo plano foi introduzido (STOP-THE-LINE). O que MUDOU: (1) todo o espaço de falhas
alcançável DENTRO do processo (retry exaurido, exceção, `OperationCanceledException`) agora É coberto por
compensação síncrona — o residual cobre apenas a queda literal do processo, não mais o caminho comum de
falha; (2) o material órfão permanece RASTREÁVEL: uma geração `Destroyed` retém seu
`PurviewSasUploadHandle.SecretStoreReference` original (nunca apagado do metadado), servindo de ledger
auditável para uma futura rotina de expurgo, mesmo sem uma implementada neste Passo.

# Threat model — I5/EPIC-06, Passo 3 (AB-I5-010: vínculo Wave↔Partition Output; AB-I5-009: fundação do
# upload Purview via AzCopy)

Delta sobre o capítulo imediatamente anterior (mesmo formato). Escopo: (a) AB-I5-010 — o vínculo IMUTÁVEL
entre uma onda aprovada e um output de particionamento canônico (Slice 4B), a fonte de autoridade de
custódia física que faltava para o upload; (b) AB-I5-009 — preparação da estrutura remota determinística,
homologação do binário AzCopy, adapter de processo isolado, persistência durável/append-only do pedido e
das tentativas de upload, job lease/fencing/heartbeat e evidência sanitizada do transporte (runbook
§25.5-§25.7). **Sem** criação/início de import job Purview, mapping CSV oficial, Graph/EXO writes,
Enable-Mailbox/auto-expansion, reconciliação pós-import ou conclusão de wave por upload isolado
(STOP-THE-LINE de [`docs/engineering/requests/AB-I5-009.md`](../engineering/requests/AB-I5-009.md)).
Nenhum destes fluxos existe no código deste Passo.

## Ativos adicionais

- **Vínculo wave→output** (`wave_partition_output_bindings`): metadado de INTEGRAÇÃO entre os bounded
  contexts Waves e PstProcessing — IDs opacos (execução, plano, parte, artefato), `part_key`/hash/tamanho
  reidratados da execução canônica, correlação. Evidência de GOVERNANÇA/integração, nunca conteúdo de
  mailbox nem segredo — `WavePartitionOutputBinding` tem construtor fechado (`Create`) que só aceita esses
  campos reidratados de um `PartitionExecutionRecord` já canônico, nunca de IDs soltos do caller.
- **Pedido lógico de upload** (`purview_upload_requests`): vínculo 1:1 durável entre uma wave e o Job
  (workload `Upload`) que a transporta — para sempre, nunca duplicado. Não carrega bindings, SAS,
  binário ou evidência.
- **Tentativas de upload** (`purview_upload_attempts`): história append-only por tentativa — desfecho,
  identidade lógica calculada (item 14), e, SOMENTE quando `Uploaded`, evidência SANITIZADA (versão/hash do
  binário AzCopy, contagem/tamanho total esperados, prefixo remoto). NUNCA SAS, NUNCA stdout/stderr bruto do
  AzCopy, NUNCA caminho físico absoluto — `PurviewUploadEvidence` só é construída no caminho de sucesso e o
  CHECK `CK_purview_upload_attempts_evidence_only_when_uploaded` reforça, no banco, que nenhum outro
  desfecho carrega esses campos.

## Classificação de dados

Nenhuma tabela nova deste Passo contém PII de mailbox/conteúdo. O SAS continua sendo o único segredo do
domínio Purview (Passo 2) — este Passo NUNCA o persiste: o adapter de upload (`AzCopyUploadProcessExecutor`)
é o ÚNICO ponto de todo o worker que chama `RedactedSecret.Reveal()`, e apenas para compor a URL de destino
imediatamente antes de iniciar o processo AzCopy — o valor nunca é atribuído a um campo, log, exceção ou
evidência persistida. O prefixo remoto (`ingestiondata/<hex opaco>`) e o nome de PST remoto
(`p_<artifactId hex>_part<NNN>.pst`) são metadados NÃO secretos, estruturalmente hexadecimais/dígitos —
nunca UPN, mailbox ou caminho de origem.

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| Caller escolhe SAS, host, container, path remoto ou PST arbitrário para o transporte (acceptance criteria 1) | O upload nunca aceita esses valores como entrada: `PurviewUploadCommandProcessor` resolve a wave via `IWaveStore`, o conjunto de PSTs via `IWavePartitionOutputBindingStore.ListForWaveAsync` (AB-I5-010, nunca `WaveSelection`/`WaveEntry.FilePath`), o SAS via `AcquireSasForUploadUseCase` (Passo 2) e o prefixo/nomes remotos via `PurviewRemoteUploadPrefix.ForWave`/`PurviewRemotePstName.ForPart` — funções puras dos IDs opacos já resolvidos, nunca de string do caller. `RequestPurviewUploadUseCase`/o pedido de upload em si carregam apenas `WaveId`. |
| Vínculo AB-I5-010 criado a partir de IDs soltos do caller, promovendo `WaveSelection` (planejamento) a prova de custódia física | `CreateWavePartitionOutputBindingUseCase` resolve a wave via `IWaveStore.GetAsync` e a execução via `IPartitionExecutionStore.FindCanonicalAsync` (os únicos stores server-side autorizados) e `WavePartitionOutputBinding.Create` reidrata plano/parte/execução/artefato/part_key/hash/tamanho EXCLUSIVAMENTE do `PartitionExecutionRecord` retornado — nunca aceita esses campos como argumento independente. `WaveEntry.FilePath`/`PstName` continuam sendo planejamento, nunca consultados pelo upload. Comprovado por `CreateReidratesPlanPartExecutionArtifactPartKeyAndOutputFromTheExecutionRecordNeverFromLooseArguments`. |
| Vínculo/execução de um tenant/projeto usado para autorizar transporte de outro (IDOR cross-scope) | `WavePartitionOutputBinding.Create` recusa estruturalmente (fail-closed, `ArgumentException`) uma execução cujo tenant/projeto diverge do escopo do vínculo — defesa em profundidade independente da Application. `IWavePartitionOutputBindingStore`/`IPurviewUploadRequestStore`/`IPurviewUploadAttemptStore` participam de `rls.tenant_isolation_policy` e filtram `project_id` explicitamente; um registro de outro tenant/projeto é indistinguível de inexistente. Comprovado por `CreateRejectsAnExecutionFromADifferentTenantThanTheBindingScope`/`...Project...` e, sob SQL Server real, `ABindingFromAnotherProjectIsIndistinguishableFromNotFound`/`ARequestFromAnotherProjectIsIndistinguishableFromNotFound`. |
| Onda ainda mutável (Draft/Validating/Blocked/ReadyForApproval) tem upload iniciado sobre uma seleção que ainda pode mudar | `RequestPurviewUploadUseCase` e, de novo, `PurviewUploadCommandProcessor.DispatchAsync` (revalidação LIVE — o estado no enfileiramento nunca é suficiente) exigem `WaveStatus.Approved`/`Frozen`; qualquer outro estado recusa fail-closed com `PurviewUploadWaveNotEligibleException`/`SourceIntegrityFailed`. Comprovado por `RequestFailsClosedWhenTheWaveIsStillMutable`. |
| Fonte PST stale/adulterada/ausente usada no transporte (item 12, acceptance criteria 5) | Antes de qualquer efeito externo, `DispatchAsync` re-resolve CADA binding via `IPartitionExecutionStore.FindCanonicalAsync` (rejeitando qualquer divergência de identidade/hash/tamanho contra o binding) e reexecuta `IPartitionPartVerifier.VerifyAsync` — a MESMA validação física de bundle/manifesto/hash já usada no réplay do Slice 4B, nunca uma verificação mais fraca. Qualquer falha (ausência, adulteração, manifesto divergente) produz `SourceIntegrityFailed` e o Job falha terminal — nenhum arquivo é sequer aberto para transporte. Comprovado por `ANonCanonicalBindingSetFailsClosedAsSourceIntegrityAndFailsTheJobWithoutTouchingAzCopy`, `APhysicallyTamperedSourceFailsClosedBeforeAnyAzCopyInvocation`. |
| Binário AzCopy substituído/desatualizado/desconhecido executa o transporte (item 5, acceptance criteria 2) | `AzCopyUploadProcessExecutor.ProbeBinaryAsync` recomputa o SHA-256 REAL do executável configurado a partir dos bytes em disco (nunca confia em configuração para o hash) e `AzCopyHomologationCatalog.IsHomologated` exige correspondência EXATA de versão E hash contra o catálogo homologado — nunca versão sozinha. A checagem ocorre ANTES de sequer adquirir o SAS (nenhum efeito externo até então). Comprovado por `IsHomologatedRequiresBothVersionAndHashToMatchExactly`, `ANonHomologatedBinaryFailsClosedBeforeAcquiringTheSas`. |
| SAS ad hoc aceito pelo request de upload, ou reaquisição fora do fluxo de claim/fencing do Passo 2 (item 3) | O upload NUNCA aceita um SAS no request — `PurviewUploadCommandProcessor` chama exclusivamente `AcquireSasForUploadUseCase.ExecuteAsync` (Passo 2, claim/lease/fencing por época já revisado por AB-I5-006/007/008), sob `WorkloadIdentities.UploadWorker`. Qualquer causa de negação (handle ausente, expirado, lease de outro adquirente, cross-scope) produz o MESMO `SasDenied` uniforme — nunca revela qual causa. Comprovado por `ASasThatCannotBeAcquiredIsRetriedRatherThanFailed`. |
| SAS exposto em log, evidence, exception, telemetria ou command line além do worker dedicado (item 7) | `RedactedSecret.Reveal()` é chamado em EXATAMENTE um ponto de todo o worker de upload (`AzCopyUploadProcessExecutor.ComposeDestinationUrl`), imediatamente antes de montar o `ProcessStartInfo.ArgumentList` — a URL composta nunca é atribuída a um campo, log ou retorno além do uso local imediato. `AzCopyProcessArgumentBuilder` usa `ArgumentList` exclusivamente (nunca shell/string concatenada). O fato de o SAS aparecer inevitavelmente no command line do processo AzCopy (documentado explicitamente aqui como limitação aceita do runbook §25.6) fica confinado a este adapter de Infrastructure, nunca a Domain/Application (reforçado estruturalmente por `VendorBoundaryTests`, que bane `ProcessStartInfo`/`System.Diagnostics.Process` de Domain/Application). |
| stdout/stderr do AzCopy (que podem refletir o SAS/path) persistidos como evidência ou propagados ao chamador (item 10) | `IAzCopyUploadExecutor.UploadFileAsync`/`AzCopyUploadFileResult` NUNCA carregam stdout/stderr — apenas `ExitCode`/`TimedOut`/`OutputLimitExceeded`, estruturalmente. `AzCopyUploadProcessExecutor` descarta o texto capturado por `ByteLimitedProcessRunner` antes de retornar. `PurviewUploadEvidence` só registra contadores/identidades já conhecidos SERVER-SIDE (do conjunto de bindings), nunca um valor reportado pelo próprio processo. |
| Estrutura remota reutilizada entre projetos/waves, ou construída a partir de nome humano/mailbox permitindo traversal (item 4, acceptance criteria 11) | `PurviewRemoteUploadPrefix.ForWave`/`PurviewRemotePstName.ForPart` são funções PURAS dos IDs opacos (tenant/projeto/wave em hex `"N"`; artefato+sequência) — estruturalmente hexadecimal/dígitos/hífen/sublinhado, sem `..`/barra/separador UNC possível. Prefixo exclusivo por (tenant, projeto, wave) — dois escopos distintos nunca colidem. Comprovado por `RemoteUploadPrefixIsExclusiveAndDifferentAcrossTenantProjectOrWave`, `RemoteUploadPrefixIsStructurallyOpaqueHexWithoutTraversalOrSeparators`, `RemotePstNameIsDerivedFromArtifactAndSequenceNeverFromMailboxOrPath`. |
| Retry/restart do worker duplica silenciosamente um upload lógico, ou perde a lineage de tentativas (item 8) | `IPurviewUploadRequestStore.EnqueueIdempotentAsync` garante, sob `UPDLOCK, HOLDLOCK` + índice único `UQ_purview_upload_requests_wave`, um único pedido/Job por (tenant, projeto, wave) PARA SEMPRE — o backstop SQL converge qualquer corrida concorrente sem duplicar. `IPurviewUploadAttemptStore` é append-only (`UQ_purview_upload_attempts_number`): cada tentativa é uma linha imutável nova, a história completa nunca é reescrita. Comprovado por `ARepeatedEnqueueForTheSameWaveConvergesWithoutCreatingASecondJob`, `TwoConcurrentEnqueuesForTheSameWaveNeverProduceTwoRequests` (concorrência real sob SQL Server). |
| Perda de job lease/fencing durante o transporte persiste `Uploaded` mesmo assim (item 9, acceptance criteria 8) | `PurviewUploadCommandProcessor` reutiliza integralmente `PlanningHeartbeat`/`IJobLeaseManager` (mesmo mecanismo de `EvExportCommandProcessor`, ADR-0003): heartbeat periódico real durante toda a execução — perda do cercamento cancela a operação ANTES de qualquer persistência nova. `IPurviewUploadAttemptStore.AppendAsync` grava sob o MESMO `JobFence` da reivindicação, revalidado (`SqlJobFence.RevalidateAsync`) imediatamente antes do commit — um lease que expirou durante a operação recusa fail-closed (`FencedOutException`), nenhuma linha é gravada. Comprovado sob SQL Server real por `AttemptAppendUnderALostFenceIsRejectedFailClosed`. |
| Exit code != 0, timeout, cancelamento ou saída de output truncada tratados como sucesso (item 11, acceptance criteria 9) | Qualquer arquivo cujo `AzCopyUploadFileResult` tenha `ExitCode != 0`, `TimedOut` ou `OutputLimitExceeded` interrompe TODO o conjunto do transporte com `ProcessFailed` — nunca um sucesso parcial "meio enviado" nem uma inferência de sucesso a partir de saída incompleta. `Uploaded` só é alcançável depois que TODOS os arquivos do conjunto canônico completaram com exit code exatamente 0. Comprovado por `AProcessFailureIsRetriedAndNeverProducesUploaded`. |
| Upload bem-sucedido confundido com importação/reconciliação Purview concluída (item 13, STOP-THE-LINE) | `PurviewUploadAttemptOutcome.Uploaded` é um estado ESTRUTURALMENTE distinto — nenhum tipo/porta deste Passo cria, inicia ou consulta um import job Purview, gera mapping CSV ou executa Graph/EXO writes; nenhuma tabela/campo deste Passo marca wave/projeto como "concluído". A wave permanece no MESMO `WaveStatus` (`Approved`/`Frozen`) após o upload — nenhuma transição de estado da wave é disparada por este Passo. |
| Réplay idempotente (item 14) reexecuta o transporte desnecessariamente, ou identidade lógica calculada incorretamente permite um falso réplay | `PurviewUploadRequestIdentity.Compute` incorpora o conjunto CANÔNICO de bindings (ordenado deterministicamente por `Execution.Value` — nunca a ordem de leitura do store, e nunca só por `PartKey`, que poderia colidir entre execuções distintas), a geração do handle SAS e o binário homologado observado; qualquer mudança real produz uma identidade diferente. `DispatchAsync` converge SEM reexecutar AzCopy quando a tentativa `Uploaded` mais recente já existe para o mesmo pedido. Comprovado por `ComputeIsDeterministicRegardlessOfBindingReadOrder`, `ComputeProducesADifferentIdentityWhenTheSasGenerationChanges`/`...BindingSetChanges`, `ASuccessfulTransportPersistsSanitizedEvidenceAndCompletesTheJob`, `AnIdempotentReplayWithTheSameIdentityNeverReRunsAzCopy`. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos módulos anteriores. |
| Identidade de manutenção usada para ler/persistir bindings, pedidos ou tentativas de upload, ou executar o transporte | Nenhum store deste Passo (`SqlWavePartitionOutputBindingStore`, `SqlPurviewUploadRequestStore`, `SqlPurviewUploadAttemptStore`) abre conexão de manutenção para escrita; apenas `SqlPurviewUploadPendingScopeReader` usa a identidade de manutenção, e é ESTRITAMENTE read-only (mesmo padrão de `SqlEvExportPendingScopeReader`) — nenhum claim/UPDATE/INSERT/efeito de negócio. `dbo.purview_upload_attempts` não concede NENHUM grant à identidade de manutenção. |
| Dependência vazando de Domain/Application/Contracts para AzCopy/`System.Diagnostics.Process`/DPAPI | Nenhum arquivo-fonte de Domain/Application/Contracts referencia `ProcessStartInfo`/`System.Diagnostics.Process` — `AzCopyProcessArgumentBuilder`/`AzCopyUploadProcessExecutor` vivem exclusivamente em `ArchiveBridge.Infrastructure`. `AzCopyHomologationCatalog`/`AzCopyBinaryIdentity` (Domain) são tipos de VALOR puros (versão + hash), sem qualquer chamada real ao AzCopy. Verificado pelos testes já existentes de `VendorBoundaryTests`/`DependencyRuleTests` (103/103 verdes com este Passo). |

## Risco residual aceito — SAS de uso único versus retry de transporte multi-arquivo (limitação documentada)

O handle SAS do Passo 2 é DELIBERADAMENTE de uso único: `Consumed` é terminal e nunca retorna a
`Available` sem um NOVO intake (nova geração, ação humana no portal Purview). Este Passo transporta N
arquivos PST de uma wave SEQUENCIALMENTE dentro de UMA ÚNICA tentativa (`DispatchAsync`), reaproveitando o
MESMO `RedactedSecret` adquirido uma vez para todos os arquivos daquela tentativa — nunca reaquire o SAS
por arquivo. Se o processo AzCopy falhar em um arquivo NO MEIO do conjunto (ex.: falha transitória de rede
no arquivo 2 de 3), a tentativa inteira falha fail-closed (`ProcessFailed`, item 11) e o Job é agendado
para retry — mas como o SAS já foi `Consumed` pela tentativa anterior, a PRÓXIMA tentativa (mesmo Job,
`AttemptNumber` seguinte) NUNCA consegue readquiri-lo: `AcquireSasForUploadUseCase` recusa fail-closed
(`SasDenied`) qualquer segunda aquisição do mesmo handle. O Job então esgota `RetryPolicy.Default` (5
tentativas) e transitina para `Failed` terminal — nunca para um falso `Uploaded`, mas também sem
recuperação automática.

**Isto é aceito, não mascarado**: (1) nenhum efeito inseguro decorre disso — o pior caso é uma falha
terminal honesta, nunca um sucesso parcial reportado como completo; (2) o mesmo comportamento já é
implícito na decisão de produto do Passo 2/runbook §25.5 (o SAS É a senha do Network Upload, de uso único
por desenho da própria Microsoft — o runbook não descreve um cenário de "SAS reutilizável para retry
parcial"); (3) inventar uma exceção a essa regra (ex.: permitir múltiplas aquisições da MESMA geração)
enfraqueceria a garantia de uso único que o Passo 2 foi desenhado e revisado (AB-I5-006/007/008) para
proteger, uma decisão de segurança que este Passo não está autorizado a reabrir unilateralmente. A
recuperação depois desse ponto exige um NOVO intake de SAS (nova geração, ação humana no portal Purview) —
o mesmo caminho operacional já documentado no runbook para qualquer SAS expirado/inválido. Registrado aqui
explicitamente para que o Engineering Reviewer decida, em Passo futuro, se um mecanismo de "reintake
automático após falha parcial" deve ser desenhado — não implementado unilateralmente aqui.

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Criação/início de import job Purview por API/automação, mapping CSV oficial, edição manual/Excel do CSV,
Graph writes/Exchange Online writes, `Enable-Mailbox -Archive`/auto-expansion/mudanças de retention,
reconciliação pós-import, marcação de wave/projeto como concluído por upload isolado, armazenamento do SAS
em plaintext/log, Azure Key Vault/Managed Identity como dependência obrigatória, reuso de pasta remota
entre projetos/waves, qualquer execução contra destino não proveniente do handle SAS canônico da wave.
Nenhum destes fluxos existe no código deste Passo — não há superfície de ameaça nova a analisar para eles
aqui.
