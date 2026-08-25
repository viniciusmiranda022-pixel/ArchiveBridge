# Threat model — I6/EPIC-07, Passo 2 (EXO Statistics Before/After & Observation Evidence Foundation)

Delta sobre o modelo de ameaças da plataforma, incorporado abaixo do capítulo do Passo anterior
([`threat-model-slice-06.md`](threat-model-slice-06.md), Passo 1 — mesmo formato). Escopo: captura,
normalização, persistência e revalidação **read-only** de estatísticas do Exchange Online Archive
before/after (runbook §25.2/§26.2), de forma tenant/project/wave/archive-scoped, versionada, idempotente e
tamper-evident (work order [`AB-I6-005.md`](../engineering/requests/AB-I6-005.md), com o gate temporal do
baseline `BeforeImport` corrigido pelo AB-I6-006) — **sem** write EXO/Graph/Purview, sem
`Enable-Mailbox`/auto-expansion/hold change, sem automação do portal Purview, sem import job automático,
sem `expected vs observed` final, sem outcome/disposition/certificate, sem conclusão de wave/projeto, sem
decommission EV e sem I7 Hardening/I8 Production Acceptance (STOP-THE-LINE do work order). Nenhum destes
fluxos existe no código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.

## Ativos adicionais

- **Snapshots de estatísticas de archive EXO** (`purview_exo_archive_statistics_snapshots`): observação
  IMUTÁVEL e versionada por `(tenant, projeto, onda, archive, fase)` — `BeforeImport`/`AfterImport`. Carrega
  apenas os campos documentados no runbook §25.2/§26.2 (status/GUIDs do archive, contadores agregados,
  último logon, holds observados) e a impressão digital das estatísticas de pasta filhas
  (`folder_count`/`folders_sha256`). Nunca contém assunto/corpo/remetente/destinatário/anexo. Ausência de
  campo do provider é `NULL` — nunca zero/false/data mínima (item 7 do work order).
- **Estatísticas de pasta filhas** (`purview_exo_archive_folder_statistics`): uma linha por pasta,
  identidade estável por `folder_path` dentro do snapshot pai, bounded (2000 pastas), deduplicada e
  canonicalizada por ordem determinística ANTES da persistência
  (`ExoArchiveFolderStatisticsSet.Canonicalize`). Contadores/datas `NULL` representam Unknown/NotReported.
  Nenhuma linha carrega conteúdo de item — apenas metadado agregado de pasta já documentado pelo runbook.

## Classificação de dados

Nenhuma das duas tabelas novas é "zero PII" no sentido absoluto — `archive_identity` é o `TargetArchiveId`
canônico (chave de agrupamento upper-invariant já usada pelo capacity gate e pelas demais evidências do
I5/I6; mesma classificação de `archive_identity` em `purview_mailbox_prechecks`) — mas nenhuma delas
contém SAS, credencial, token, conteúdo de e-mail, transcript PowerShell bruto ou stack trace. O adapter
substituível (`IExoArchiveStatisticsAdapter`) entrega valores já estruturados (enum/GUID/`long?`/
`DateTimeOffset?`) — nenhum valor formatado/localizado é parseado por regex/heurística de string (item 8),
eliminando uma classe inteira de bugs de injeção/locale que afetariam um parser de texto. `folder_path`/
`folder_type` são bounded (400/100 caracteres) e validados via `TextValue.Require` (sem caractere de
controle) antes de qualquer persistência — path de pasta é metadado operacional necessário à
reconciliação (runbook §26.2 lista `FolderPath` explicitamente como evidência), não é tratado como
segredo, mas nunca aparece em log/auditoria fora da própria linha persistida (não há canal de log
separado neste Passo — a evidência append-only É a trilha de auditoria, mesmo princípio do Passo 1).

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| `AfterImport` capturado antes de o provider ter reportado conclusão suficiente do import, mascarando um estado pré-import como pós-import | `CaptureExoArchiveStatisticsUseCase.ExecuteAfterImportAsync` reaproveita `EvaluatePurviewServiceResultCompletenessUseCase` (Passo 1) e recusa fail-closed com `ExoArchiveStatisticsPrerequisiteException` — **sem sondar o adapter** — a menos que o desfecho seja `CompleteForProviderEvidence` (todo PST canônico da onda com status/contadores conclusivos). `ImportCompleted`/`Complete` observado permanece apenas evidência do provider; nenhum caso de uso deste Passo marca onda/projeto como concluído. |
| `BeforeImport` capturado DEPOIS que a execução do import já começou/terminou (ex.: já existe observação `ImportStarted`/`ImportCompleted`/`ImportFailed`), rotulando um estado pós-import como "baseline anterior" e contaminando o futuro `expected vs observed` mesmo com hashes/CI corretos (AB-I6-006) | `CaptureExoArchiveStatisticsUseCase.ExecuteBeforeImportAsync` verifica, ANTES de sondar o adapter, TODOS os planos de import job já existentes para a onda (`IPurviewImportJobStore.GetPlansForWaveAsync`) e recusa fail-closed (`ExoArchiveStatisticsPrerequisiteException`) se a observação mais recente de QUALQUER um deles indicar `ExoBeforeImportEligibility.IsImportExecutionStartedOrBeyond` (o estado observado mais precoce/inequívoco de início real de `Import data`, runbook §25.9 item 79 — `JobCreated`/`ValidationAttached`/`AnalysisCompleted` continuam permitidos, pois representam apenas planejamento/validação, nunca execução). A decisão vem inteiramente de evidência server-side — nenhum identificador ou timestamp fornecido pelo caller decide o boundary. Um baseline já capturado ANTES do boundary continua legível/revalidável (`GetLatestAsync`/`GetFoldersAsync` não são afetados); apenas uma NOVA captura depois do boundary é bloqueada, sem criar versão N+1. |
| Mailbox/archive fornecido pelo caller usado diretamente na sondagem do adapter (IDOR) | `CaptureExoArchiveStatisticsUseCase` resolve a `ArchiveRef` canônica a partir de `wave.Selection.Entries` via `IWaveStore` (mesmo padrão anti-IDOR de `SubmitMailboxPrecheckUseCase`/AB-I5-003) — o caller fornece apenas `TargetArchiveId` (identificador opaco). Onda inexistente, archive fora da seleção ou archive sem identidade resolvida produzem TODOS o mesmo `ExoArchiveStatisticsSourceNotFoundException`, sem sondar o adapter e sem vazar existência/UPN/GUID de outro escopo. |
| Archive/onda de outro tenant/projeto acessado por IDOR na leitura de snapshots persistidos | Toda leitura (`GetLatestAsync`, `GetFoldersAsync`) filtra `project_id` explicitamente e participa de `rls.tenant_isolation_policy` (FILTER + BLOCK) nas duas tabelas novas — um snapshot de outro escopo é indistinguível de inexistente. |
| Campo ausente do provider (ex.: `ItemCount`, holds) convertido em zero/false, mascarando dados realmente ausentes como valores reais | Todo contador é `long?`, toda data é `DateTimeOffset?` e os três flags de hold/auto-expansion são `bool?` — o adapter nunca é obrigado a inventar um valor; `ExoArchiveStatisticsSnapshot.Create`/`Rehydrate` preservam `null` sem coerção. Comprovado por `CreateAllowsEveryOptionalFieldAsNullUnknown`/`BeforeImportPreservesUnknownFieldsAsNullNeverAsZeroOrFalse` (Domain/Integration). |
| Estatística de pasta duplicada, oversized, ou com data temporalmente impossível aceita silenciosamente | `ExoArchiveFolderStatistic` recusa (fail-closed, `ArgumentException`/`ExoArchiveStatisticsValidationException`) path/tipo vazio ou oversized, contador negativo e `OldestItemReceivedDateUtc` posterior a `NewestItemReceivedDateUtc`; `ExoArchiveFolderStatisticsSet.Canonicalize` recusa mais de 2000 pastas e `FolderPath` duplicado ANTES de qualquer persistência — reforçado no BANCO por `CK_peafs_date_order`/`CK_peass_folder_count` (defesa em profundidade). |
| Mesma observação lógica reenviada (retry) produzindo versões duplicadas, ou uma mudança real perdida sob concorrência | `SqlExoArchiveStatisticsStore.PersistAsync` locka (`UPDLOCK, HOLDLOCK`) TODAS as versões existentes do escopo (tenant/projeto/onda/archive/fase) na MESMA transação e decide, sob esse lock, a próxima versão E se alguma já existente converge pelo MESMO `observation_hash` (mesmo padrão de `SqlPurviewServiceResultReportStore.PersistAsync`, AB-I6-003 Blocker 3) — chamadas concorrentes com conteúdo idêntico convergem; conteúdo genuinamente diferente aloca N+1 sem perder nenhuma das duas. O índice único `UQ_peass_observation (wave_id, archive_identity, phase, observation_hash)` é o backstop no BANCO. |
| Snapshot ou estatística de pasta persistidos adulterados diretamente no SQL e lidos como canônicos | Mesma fronteira NÃO CONFIÁVEL do Passo 1: `ExoArchiveStatisticsSnapshot.Rehydrate` recomputa `observation_hash`/`snapshot_hash` a partir dos campos REALMENTE carregados e recusa fail-closed qualquer divergência (inclusive adulteração da própria coluna `phase`, que muda a identidade lógica do registro); `SqlExoArchiveStatisticsStore` recarrega as estatísticas de pasta filhas em TODO caminho que trata uma versão como evidência canônica (`GetLatestAsync`, `GetFoldersAsync`, o ramo de convergência de `PersistAsync`) e recusa fail-closed se a contagem divergir de `folder_count` ou o hash agregado recomputado (`ExoArchiveFolderStatisticsHash`) divergir de `folders_sha256` — inserção/remoção/duplicação/alteração de qualquer pasta é detectada, não só alteração de campo. |
| Holds/retention/auto-expansion observados interpretados como autorização/execução de mudança | `RetentionHoldEnabled`/`LitigationHoldEnabled`/`AutoExpandingArchiveEnabled` são exclusivamente campos de OBSERVAÇÃO (`bool?`) em `ExoArchiveStatisticsSnapshot` — nenhum caso de uso deste Passo, nenhuma porta (`IExoArchiveStatisticsAdapter`) e nenhuma tabela nova expõe qualquer operação de mutação; o adapter é estritamente `ObserveAsync` (somente leitura, documentado explicitamente como sem efeito colateral). |
| `ExoStatisticsPhase`/resultado deste Passo confundido com resultado de reconciliação final | O enum `ExoStatisticsPhase` expõe SOMENTE `BeforeImport`/`AfterImport` — nenhum valor `Pass`/`Fail`/`Certificate`/`Completed` existe (comprovado por `PhaseNeverExposesAFinalReconciliationOutcome`, Domain); nenhum caso de uso deste Passo referencia `ArchiveBridge.Domain.Reconciliation.ReconciliationOutcome` nem qualquer API de fechamento de onda/projeto. |
| Dependência vazando de Domain/Application para ExchangeOnlineManagement/PowerShell/Graph/vendor SDK concreto | Nenhum pacote/assembly de fornecedor é referenciado por `ArchiveBridge.Domain`/`ArchiveBridge.Application`/`ArchiveBridge.Contracts` deste módulo — `IExoArchiveStatisticsAdapter` é uma porta pura (registros/enum/`Task`), sem tipo de fornecedor na assinatura; este Passo não inclui nenhuma implementação real de `Get-EXOMailboxStatistics`/`Get-EXOMailboxFolderStatistics` (a porta e o boundary fail-closed bastam — nenhum adapter fake/estrutural é promovido a produção, item 20). `VendorBoundaryTests.VendorAssemblyPattern` foi estendido com `exchangeonlinemanagement`/`microsoft\.exchange` para que uma futura implementação vendor-dependente adicionada fora de `Infrastructure`/`Workers` seja pega fail-closed pelo teste de arquitetura. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos módulos anteriores. |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

`Enable-Mailbox`/`Set-Mailbox`/auto-expansion ou qualquer write EXO/Graph, remoção/alteração de
Retention/Litigation Hold ou policy, criação/validação/início automático de import job Purview ou
`Import data`, automação do portal Purview, `expected vs observed` final, outcomes finais
`PASS`/`PASS_WITH_EXPLAINED_EXCEPTIONS`/`INCONCLUSIVE`/`FAIL`/`DUPLICATE_RISK` como decisão da wave,
exception disposition, certificate/evidence package final, conclusão de wave/projeto, decommission EV,
I7 Hardening ou I8 Production Acceptance. Nenhum destes fluxos existe no código deste Passo — não há
superfície de ameaça nova a analisar para eles aqui.
