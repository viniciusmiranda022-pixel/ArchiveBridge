# Threat model — Slice 4B, Passo 1 (PST Inspection & Inventory)

Delta sobre o modelo de ameaças da plataforma. Escopo: inspeção **read-only, local** de PSTs já sob
custódia autorizada. Não há Export-EVArchive, split/partition, upload/AzCopy, Purview/Graph/EXO nem
importação nesta fatia (ver STOP-THE-LINE em
[`vertical-slice-04b-pst-inspection.md`](../engineering/vertical-slice-04b-pst-inspection.md)).

## Ativos

- **Metadados de custódia** por tenant/projeto: identidade opaca do artefato (`artifact_id`), caminho
  relativo à raiz configurada, SHA-256 e tamanho do PST. **Não** há conteúdo de mensagem, assunto, corpo,
  destinatário ou anexo nestas tabelas.
- **Checkpoints de inspeção**: diagnóstico estrutural sanitizado, variante de formato, hash/tamanho
  observados, nome/versão da engine, correlação — evidência operacional, não conteúdo de mailbox.
- **O PST em si**, em repouso na raiz de custódia local (NTFS/NAS/SMB) — nunca copiado, nunca modificado
  por este Passo.

## Classificação de dados (custódia)

As tabelas `dbo.pst_artifacts`/`dbo.pst_inspections` **não são "zero PII"**: o caminho relativo do artefato
e o diagnóstico estrutural são metadados operacionais atribuíveis à custódia de um PST específico. O que
elas **não** contêm: bytes do PST, assunto/corpo/remetente/destinatário de qualquer mensagem, nome de
anexo, ou qualquer valor extraído do conteúdo do mailbox — a engine deste Passo nunca percorre a árvore
NDB (ver decisão de adapter no documento do slice), então não há como um item de mensagem individual
aparecer na evidência.

## Ameaças e mitigações

| Ameaça | Mitigação |
| --- | --- |
| Vazamento cross-tenant | Leituras ocorrem sob `SESSION_CONTEXT('tenant_id')` (RLS) — `dbo.pst_artifacts`/`dbo.pst_inspections` participam de `rls.tenant_isolation_policy` (FILTER + BLOCK AFTER INSERT), igual às tabelas de custódia existentes (Slice 2/3/4A). |
| Vazamento cross-project (IDOR) | Além da RLS, `IPstCustodyStore.FindAsync` filtra explicitamente por `project_id`. Um `ArtifactId` de outro projeto do mesmo tenant retorna `null` — `InspectPstArtifactUseCase` lança `PstArtifactNotFoundException`, indistinguível de "não existe" (nenhuma enumeração revela existência). Comprovado por `CrossTenantAndCrossProjectAreDeniedIndistinguishablyFromNotFound`. |
| Path traversal / escape da raiz de custódia | `PstRelativePath` (Domain) rejeita, na FORMA do texto, caminho absoluto (incluindo rótulo de unidade Windows — verificação deliberadamente independente de `Path.IsPathRooted`, cujo resultado varia por plataforma) e segmentos de travessia (`.`/`..`). Em Infrastructure, `ArtifactPathContainment.EnsureContained` (já usado por `FileSystemMappingArtifactStore`) canonicaliza contra a raiz configurada e rejeita qualquer symlink/reparse point na cadeia — inclusive no componente final (o próprio arquivo) — antes de abrir. Defesa em profundidade: forma no Domain, I/O real em Infrastructure. Comprovado por `SymlinkedArtifactPathIsRejectedFailClosedAsReadError`. |
| TOCTOU (arquivo trocado entre custódia e leitura) | O hash é sempre recalculado NA LEITURA (streaming, cobrindo o arquivo inteiro) e comparado ao hash registrado em custódia; divergência ⇒ `Stale`, fail-closed, nunca reaproveita um resultado anterior. Arquivo removido/inacessível entre a resolução do caminho e a abertura vira `ReadError` sanitizado, nunca um crash não tratado. |
| TOCTOU (symlink/reparse point trocado entre a checagem de contenção e a abertura) | `ArtifactPathContainment.EnsureContained` roda DUAS VEZES: antes de abrir o `FileStream` e novamente logo após, antes de qualquer leitura — qualquer reparse point detectado em qualquer uma das duas checagens falha fechado como `ReadError` sem ler/hashear conteúdo. **Alegação corrigida (AB-4B-002 item 3):** isto ESTREITA a janela entre checagem e abertura, mas NÃO é uma garantia atômica — as duas checagens reexaminam o caminho no sistema de arquivos, não o handle/descritor já aberto; um atacante que vença a corrida em ambas as janelas ainda poderia, em teoria, fazer a engine abrir um arquivo fora da raiz. Uma garantia livre de corrida exigiria verificação baseada no handle aberto (API específica de plataforma via P/Invoke — ex.: resolver o destino real do descritor no Windows), fora do escopo deste Passo sem novo ADR. Residual: mesmo nesse cenário-limite, a engine nunca ESCREVE no arquivo (somente leitura) e o hash observado ainda é comparado ao hash de custódia — mas o diagnóstico estrutural retornado poderia refletir um arquivo diferente do PST registrado. |
| Arquivo malformado/hostil derruba o worker ou produz sucesso falso | A engine nunca lança exceção não tratada para um arquivo ilegível/inválido/truncado — todo erro de leitura vira `PstStructuralDiagnostic.ReadError`; um cabeçalho que não bate com a assinatura PST vira `InvalidSignature`/`InvalidClientSignature`/`UnsupportedVersion`/`TooSmall`, nunca `Valid`. Nenhum destes diagnósticos executa parser de terceiro (sem superfície de exploit de biblioteca de PST). |
| Exaustão de recursos (arquivo enorme, leitura infinita) | `PstStorageOptions.MaxSizeBytes` rejeita fail-closed (`PstInspectionLimitExceededException`, outcome `LimitExceeded`) antes de abrir o stream; `PstStorageOptions.Timeout` aborta via `CancellationTokenSource.CancelAfter` distinto do cancelamento do chamador. Nenhum destes casos é reportado como sucesso. |
| Replay/duplicação de efeito | Réplay idempotente: mesmo artefato + mesmo hash de custódia ⇒ resultado canônico reaproveitado, a engine NÃO é reinvocada (`FindCanonicalAsync` antes de `InspectAsync`). |
| Corrida de gravação (dois workers inspecionam o mesmo artefato ao mesmo tempo) | Índice único **filtrado** `UX_pst_inspections_canonical (tenant_id, project_id, artifact_id, expected_hash) WHERE is_canonical = 1` é o backstop SQL — `is_canonical` é gravado pela Application com o mesmo valor que `PstInspectionRecord.IsCanonical` calcula no Domain (outcome=Completed E hash observado bate com o esperado), nunca um filtro que reimplemente a regra de forma divergente, e um CHECK constraint trava no banco que esse valor nunca diverge da regra (**correção AB-4B-002 item 1**: a versão original filtrava só por `outcome = 0`, o que fazia `ReadError` — que também é `Completed` mas sem hash confiável — competir pelo índice de canônicos). A Application captura `PstInspectionConflictException` e relê o canônico já persistido (nunca duas linhas canônicas para o mesmo artefato/hash); se a releitura pós-conflito não encontrar nenhum canônico, falha fechado com `PstInspectionConflictUnresolvedException` em vez de devolver um registro não persistido (**correção AB-4B-002 item 2**). A tradução de `SqlException` 2601/2627 para conflito é restrita à violação do índice `UX_pst_inspections_canonical` especificamente. Comprovado por `ConcurrentInspectionOfTheSameArtifactConvergesToExactlyOneCanonicalRecord` (6 chamadas concorrentes ⇒ 1 linha canônica) e `ConcurrentReadErrorAttemptsAreNotConfusedWithACanonicalRace`. |
| Custódia registrada duas vezes para o mesmo caminho | `UQ_pst_artifacts_path (tenant_id, project_id, relative_path)` impede duplicidade silenciosa de registro dentro do mesmo escopo. |
| Vazamento de caminho real/stack trace em log/evidência | Todo erro de I/O é capturado e sanitizado em `PstStructuralDiagnostic.ReadError`/`PstInspectionLimitExceededException` (`ReasonCode` curto, sem interpolar caminho ou mensagem de exceção bruta). |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada. |

## Fora de escopo desta fatia (herdado do STOP-THE-LINE)

Export-EVArchive, split/partition execution, repair de PST, Outlook automation, upload/AzCopy/Azure
staging, Purview/Graph/Exchange Online/import job, reconciliação M365. Nenhum destes fluxos existe no
código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.

---

# Delta — Slice 4B, Passo 2 (Partition Planning)

Escopo adicional: **planejamento** determinístico de particionamento sobre PSTs já canonicamente
inspecionados. Continua sem execução de split/rewrite/repair, sem PST de saída, sem Export-EVArchive,
upload/AzCopy, Purview/Graph/EXO e sem importação (ver STOP-THE-LINE em
[`vertical-slice-04b-partition-planning.md`](../engineering/vertical-slice-04b-partition-planning.md)).

## Ativos adicionais

- **Planos de particionamento** (`dbo.pst_partition_plans`): identidade determinística (`plan_hash`),
  fingerprint/limites da política, nome/versão de planner e engine, desfecho/motivo sanitizados, correlação
  e timestamp. Evidência decisória — **não** contém conteúdo de mailbox.
- **Partes planejadas** (`dbo.pst_partition_plan_parts`): chave opaca derivada do `plan_hash`, sequência e
  tamanho planejado. É INTENÇÃO: nenhum arquivo de parte existe.

## Classificação de dados

As duas tabelas novas **não guardam caminho físico, nome de arquivo, UPN, assunto, corpo, destinatário ou
anexo** — não há sequer coluna capaz de carregá-los, e `part_key` é um hash opaco derivado do `plan_hash`
(runbook §20.1: "nomes não incluem UPN completo; usar IDs opacos"). Comprovado lendo TODAS as colunas
persistidas em `ThePersistedPlanCarriesNoPathFileNameOrMailboxIdentifier`. O que resta é metadado
operacional atribuível a um artefato de custódia, na mesma classificação das tabelas do Passo 1.

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| Plano gerado sobre um PST que mudou desde o registro (planejar sobre origem obsoleta) | A inspeção canônica é buscada SEMPRE pelo hash REGISTRADO em custódia; origem alterada não tem canônico para esse hash ⇒ `Blocked / CanonicalInspectionUnavailable`. O Domain ainda revalida `IsCanonical` e a igualdade `expected == observed == registrado` (`Blocked / SourceHashDivergence`). Comprovado por `AnArtifactChangedAfterRegistrationCannotBePlanned`. |
| Plano "executável" fabricado sem informação suficiente (boundaries/contagens inventados) | Só existe UM caso planejável sem inventário: o artefato inteiro dentro do `TargetPartBytes` (nenhum split necessário). Acima disso ⇒ `Unsupported / ItemInventoryUnavailable` com ZERO partes, travado também no banco (`CK_pst_partition_plans_outcome_fields`). Comprovado por `AnArtifactAboveTheTargetIsUnsupportedAndPersistsNoPartsAtAll`. |
| Reaproveitar plano obsoleto após mudança de política/configuração | O fingerprint da política (`canonicalJson`) entra no `plan_hash`; qualquer limite diferente produz identidade diferente e o índice único é por identidade. Comprovado por `ChangingThePolicyProducesANewIdentityAndNeverReusesThePreviousPlan`. |
| Colisão de identidade entre tenants/projetos com conteúdo idêntico | `plan_hash` inclui tenant, projeto, artefato e inspeção além do hash de origem (extensão explícita sobre a fórmula do runbook §20.2). Comprovado por `TwoArtifactsWithIdenticalContentInDifferentProjectsNeverShareAPlanIdentity`. |
| Vazamento cross-tenant/cross-project (IDOR) do plano | RLS (`rls.tenant_isolation_policy`, FILTER + BLOCK AFTER INSERT) nas duas tabelas + filtro explícito por `project_id` nas leituras + `PstArtifactNotFoundException` indistinguível de "não existe". Mesmo conhecendo o `plan_hash` exato, outro escopo lê `null`. Comprovado por `CrossTenantAndCrossProjectPlanningIsDeniedIndistinguishablyFromNotFound`. |
| Plano referenciando inspeção de outro escopo/artefato | FK composta `FK_pst_partition_plans_inspection (inspection_id, tenant_id, project_id, artifact_id)` — o banco recusa a linha, não apenas a aplicação. |
| Corrida de gravação (dois workers planejam o mesmo artefato) | Índice único filtrado `UX_pst_partition_plans_canonical ... WHERE is_canonical = 1`; a Application captura `PartitionPlanConflictException` e relê o canônico; releitura vazia ⇒ `PartitionPlanConflictUnresolvedException` (nunca devolve plano não persistido). Tradução de `SqlException` 2601/2627 restrita a esse índice. Comprovado por `ConcurrentPlanningOfTheSameArtifactConvergesToExactlyOneCanonicalPlan`. |
| Plano parcial (linha sem suas partes) sobrevivendo a falha | Plano e partes são gravados na MESMA transação; qualquer falha desfaz tudo antes de propagar. |
| Store devolvendo como "canônico" algo que não é | A Application revalida `PartitionPlan.IsCanonical`, a identidade PEDIDA e a auto-consistência da identidade (`HasConsistentIdentity()`) antes de reaproveitar (`PartitionPlanCanonicityViolationException`); a store ainda revalida que o fingerprint de política persistido bate com os limites persistidos. |
| Linha canônica persistida corrompida/adulterada sendo reidratada e reaproveitada em réplay | Nenhum `CHECK` row-local relaciona plano e partes, então o banco sozinho aceita partes incoerentes. `PartitionPlan.Rehydrate` aplica os MESMOS invariantes estruturais de `Create` (caminho único `FindStructuralViolation`: partes contíguas 1..N, soma exatamente igual a `source_size_bytes`, nenhuma acima de `hard_part_bytes`, `covers_entire_source` coerente, campos obrigatórios presentes) e recalcula a identidade determinística a partir das PRÓPRIAS entradas persistidas (`plan_hash` e cada `part_key`). Divergência ⇒ `PartitionPlanIntegrityViolationException`; nada é normalizado nem devolvido. Comprovado por `ReplayOfAPersistedCanonicalPlanWhosePartsDoNotSumToTheSourceFailsClosed`, `...WithANonContiguousPartSequenceFailsClosed`, `...WithAPartAboveTheHardLimitFailsClosed`, `ReplayOfASinglePartCanonicalPlanThatDoesNotCoverTheEntireSourceFailsClosed` e `ReplayOfACanonicalPlanWhoseStoredHashDoesNotMatchItsPersistedInputsFailsClosed`. |
| Escalada silenciosa de capacidade (planejar sem inspeção habilitada) | `PstPartitionPlanning:Enabled=false` por padrão; habilitar sem `PstInspection:Enabled=true` derruba o host no startup em vez de ignorar a intenção do operador. Comprovado por `PlanningEnabledWithoutInspectionBringsTheHostDownInsteadOfBeingSilentlyIgnored`. |
| Execução acidental de split/escrita a partir do planejamento | O caso de uso de planejamento não recebe `IPstEngine` nem qualquer porta capaz de abrir/escrever o PST — não há caminho de código, não apenas ausência de chamada. `IPartitionPlanner.Plan` é síncrono, sem `CancellationToken` e sem I/O. Comprovado por `PlanningHasNoPathToThePstEngineOrAnyOtherWriteCapableDependency`. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada. |

## Fora de escopo (herdado do STOP-THE-LINE do Passo 2)

Execução de particionamento, criação de PST de saída, repair, parser/vendor não aprovado por ADR,
Export-EVArchive real, AzCopy/Azure staging, Purview/Graph/Exchange Online/import job, validação e
reconciliação pós-partição, CSV builder de importação e reconciliação final M365. Nenhum destes fluxos existe
no código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.

---

# Delta — Slice 4B, Passo 3 (Partition Execution Foundation)

Escopo adicional: **execução** — a única operação autorizada é materializar
`Planned / SinglePartWithinTarget` como cópia byte-for-byte verificada. Continua sem split/rewrite real, sem
parser/vendor novo, sem Export-EVArchive, upload/AzCopy, Purview/Graph/EXO e sem importação (ver
STOP-THE-LINE em [`vertical-slice-04b-partition-execution.md`](../engineering/vertical-slice-04b-partition-execution.md)).

## Ativos adicionais

- **Execuções de partição** (`dbo.pst_partition_executions`): IDs opacos (execução, plano, parte, artefato),
  identidade determinística (`plan_hash`/`part_key`/`part_sequence`), hash/tamanho de origem e de saída, nome/
  versão do executor, correlação e timestamps. Manifesto durável do `PartCreated` — **não** contém conteúdo
  de mailbox nem caminho físico.
- **O output materializado** (`part.pst` + `part.sha256` + `manifest.json`), em repouso sob a raiz de output
  configurada, em um caminho DERIVADO DE IDS OPACOS (nunca de UPN/nome de arquivo/caminho da origem). É a
  única capacidade deste Slice que cria um arquivo NOVO — sempre uma cópia, nunca o PST original.

## Classificação de dados

A tabela `dbo.pst_partition_executions` e o `manifest.json` do bundle **não guardam caminho físico, nome de
arquivo, UPN, assunto, corpo, destinatário ou anexo** — não há sequer coluna/campo capaz de carregá-los; o
local físico do output é sempre derivado, em tempo de leitura, dos IDs opacos já persistidos. Comprovado
lendo TODAS as colunas persistidas E o `manifest.json` inteiro em
`ThePersistedExecutionAndManifestCarryNoPathFileNameOrMailboxIdentifier`. O que resta é metadado operacional
atribuível a uma execução específica, na mesma classificação das tabelas dos Passos 1/2.

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| Execução de um plano inelegível (Unsupported/Blocked/não-canônico/múltiplas partes) | `ExecutePartitionPlanUseCase.EnsureEligible` recusa ANTES de qualquer I/O — zero arquivo, zero linha SQL. Comprovado por `AnUnsupportedPlanIsRejectedWithZeroOutputAndZeroExecutionRows`, `ABlockedPlanIsRejectedWithZeroOutputAndZeroExecutionRows`, `ForgedPlanIdentityIsRejectedBeforeAnyIO`. |
| Output que não é cópia byte-for-byte da origem (bug de escrita, parser silenciosamente corrompendo bytes) | Reforçado em DUAS camadas independentes: `PartitionExecutionRecord.Complete`/`Rehydrate` no Domain exige `OutputHash == SourceHash` e `OutputSizeBytes == SourceSizeBytes`; `CK_pst_partition_executions_byte_identical` trava o mesmo invariante no banco. Nenhuma das duas confia na outra. |
| Origem alterada entre o planejamento e a execução (staleness) | O writer sempre lê a origem AO VIVO e recalcula o hash durante a cópia — divergência contra `plan.Source.SourceHash` aborta ANTES de publicar qualquer output (`PartitionExecutionSourceStaleException`, staging descartado). Comprovado por `SourceThatDriftedOnDiskAfterPlanningIsRejectedBeforeAnyCanonicalOutputIsPublished`. |
| Output publicado prematuramente (antes de verificado) | Protocolo de 3 checkpoints: staging + reabertura (checkpoint 1) → `Directory.Move` atômico + reabertura do bundle final (checkpoint 2) → INSERT SQL (checkpoint 3). Nenhum checkpoint anterior é pulado; a Application só recebe o resultado depois do checkpoint 2. |
| Crash entre a publicação do output e o checkpoint SQL | O restart detecta o bundle já publicado no caminho determinístico, reconfere (nunca confia na mera existência do arquivo) e converge sem reescrever — só então persiste o checkpoint SQL que faltava. Comprovado por `ACrashAfterFinalizationButBeforeThePersistCheckpointConvergesWithoutDuplicating`. |
| Adulteração do output já publicado | Toda reutilização do caminho final reabre e reconfere hash/tamanho/manifesto ANTES de confiar — divergência nunca é sobrescrita automaticamente (`PartitionExecutionOutputTamperedException`). Isso cobre DOIS caminhos independentes: (1) réplay via filesystem quando o checkpoint SQL ainda não existe (crash-recovery), comprovado por `TamperingTheExistingOutputIsDetectedOnReadbackAndNeverSilentlyOverwritten`; e (2) réplay de um checkpoint SQL JÁ PERSISTIDO — **correção AB-4B-007**: a versão original devolvia a execução canônica persistida direto da consulta SQL, sem qualquer revalidação física, então tampering/corrupção/remoção do bundle DEPOIS que o checkpoint existia passava despercebido. `ExecutePartitionPlanUseCase` agora chama `IPartitionPartVerifier.VerifyAsync` (implementado por `LocalSinglePartExecutionVerifier`, somente leitura) antes de devolver qualquer execução persistida como canônica — reconfere hash/tamanho de `part.pst`, o sidecar `part.sha256` e o CONTEÚDO estrutural de `manifest.json` (IDs opacos, `plan_hash`, `part_sequence`/`part_key`, hash/tamanho, identidade do executor) contra o registro persistido; o manifesto é sempre evidência, nunca fonte de verdade da verificação. Comprovado por `ReplayAfterThePersistedCheckpointDetectsByteAlterationOfThePartFileAndFailsClosed`, `ReplayAfterThePersistedCheckpointDetectsARemovedBundleFileAndFailsClosedWithoutRecreatingIt` e `ReplayAfterThePersistedCheckpointDetectsManifestOnlyTamperingEvenWithAValidPartAndSidecar`. Consistente com o runbook §20.5: um part perdido/adulterado nunca é regenerado silenciosamente. |
| Diretório de staging órfão confundido com sucesso | A canonicidade é decidida SOMENTE pelo bundle validado no caminho FINAL — staging nunca é lido como evidência de conclusão. `TempStagingReconciler` (operação de manutenção explícita, nunca automática) remove staging mais antigo que um limiar de idade seguro. Comprovado por `AnOrphanStagingDirectoryIsNeverConfusedWithACompletedPartAndIsSafelyReconciled`. |
| Corrida de escrita (dois workers executam o mesmo plano ao mesmo tempo) | Convergência em DUAS camadas: filesystem (o writer detecta o bundle final já publicado e reconfere em vez de reescrever) e SQL (índice único `UX_pst_partition_executions_canonical (tenant_id, project_id, plan_id, part_id)`; conflito de INSERT relê o canônico; releitura vazia ⇒ `PartitionExecutionConflictUnresolvedException`). Comprovado por `ConcurrentExecutionOfTheSamePlanConvergesToExactlyOneCanonicalResult`. |
| Exaustão de espaço em disco | Preflight (`DriveInfo.AvailableFreeSpace`) ANTES de abrir qualquer stream de escrita — nunca inicia uma cópia que não teria como terminar. Comprovado por `InsufficientDiskSpacePreflightFailsClosedWithoutWritingAnything`. |
| Cópia pendurada indefinidamente (I/O travado, disco lento) | `CancellationTokenSource` vinculado com `CancelAfter(Timeout)`, distinto do cancelamento do chamador — nenhum output canônico é publicado em nenhum dos dois casos. Comprovado por `ACopyThatNeverCompletesIsAbortedByTheConfiguredTimeoutWithoutPublishingAnyOutput`, `CallerCancellationDuringTheCopyNeverPublishesAnyOutput`. |
| Path traversal / escape da raiz de output ou de origem | `ArtifactPathContainment.EnsureContained` protege TODO caminho resolvido (origem, staging root, staging dir, diretório final) — mesma defesa já usada em `HeaderOnlyPstInspectionEngine`/`FileSystemMappingArtifactStore`, incluindo rejeição de symlink/reparse point na cadeia. |
| Vazamento cross-tenant/cross-project (IDOR) da execução | `IPartitionPlanStore.FindByIdAsync` filtra por escopo (RLS + `project_id` explícito); `IPartitionExecutionStore.FindCanonicalAsync` idem. Mesmo conhecendo o `PartitionPlanId` exato, outro escopo lê "não encontrado", indistinguível de "não existe". Comprovado por `CrossTenantAndCrossProjectExecutionIsDeniedIndistinguishablyFromNotFound`. |
| Store devolvendo como "canônica" uma execução que não é | A Application revalida `HasConsistentIdentity()` e o `PlanHash` PEDIDO antes de reaproveitar (`PartitionExecutionCanonicityViolationException`). |
| Linha canônica persistida corrompida/adulterada sendo reidratada e reaproveitada | `PartitionExecutionRecord.Rehydrate` aplica os MESMOS invariantes estruturais de `Complete` (identidade de parte consistente, output byte-for-byte, timestamps coerentes); divergência ⇒ `PartitionExecutionIntegrityViolationException`, nada é normalizado nem devolvido. |
| Escrita a partir da execução vazando para fora da raiz de output configurada | `PstPartitionExecutionOptions.ValidateForOperation` exige `OutputRootPath` distinto de `PstInspection:RootPath`, validado no STARTUP (nunca detectado só na primeira execução real). |
| Escalada silenciosa de capacidade (executar sem planejamento habilitado) | `PstPartitionExecution:Enabled=false` por padrão; habilitar sem `PstPartitionPlanning:Enabled=true` derruba o host no startup. Comprovado por `ExecutionEnabledWithoutPlanningBringsTheHostDownInsteadOfBeingSilentlyIgnored`. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada. |

## Fora de escopo (herdado do STOP-THE-LINE do Passo 3)

Parser/writer/splitter Aspose, libpff ou qualquer vendor novo; split multi-part real; semantic partitioning
por item/pasta/data/bin-packing; repair/ScanPST/quarantine workflow além de registrar falha necessária;
Export-EVArchive real; AzCopy/Azure staging/SAS; Purview, Graph, Exchange Online ou import job; CSV builder
do Purview; reconciliação final M365; regeneração automática de part já usada em import. Nenhum destes
fluxos existe no código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.
