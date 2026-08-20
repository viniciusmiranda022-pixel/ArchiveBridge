# Vertical Slice 4B — Partition Planning (Passo 2)

## Status

**Em desenvolvimento — PR deve permanecer em Draft até `ARCHIVEBRIDGE_MERGE_APPROVED` explícito do
Engineering Reviewer para o HEAD SHA corrente, com CI totalmente verde.**

Work order versionado: [`docs/engineering/requests/AB-4B-004.md`](requests/AB-4B-004.md) (`REQUEST_ID: AB-4B-004`).
Passo anterior (mergeado): [PST Inspection & Inventory](vertical-slice-04b-pst-inspection.md).

## Objetivo

Evoluir o Slice 4B para o **Partition Engine em modo de PLANEJAMENTO**: produzir, para um PST canonicamente
inspecionado, um plano de particionamento **determinístico, persistido e auditável** — sem executar split,
rewrite, repair, sem criar PST de saída e sem qualquer staging/importação externa
(ver [Fora do escopo](#fora-do-escopo--stop-the-line)).

Planejar é **intenção/evidência**, não execução. Nada neste Passo abre, escreve ou divide um PST: o caso de
uso de planejamento sequer recebe a porta `IPstEngine` (provado por
`PlanPstPartitionUseCaseTests.PlanningHasNoPathToThePstEngineOrAnyOtherWriteCapableDependency`).

## Política: derivada do runbook, nunca inventada

Os limites vêm exclusivamente da política já documentada no **runbook §20.1**:

| Parâmetro | Valor | Origem |
| --- | --- | --- |
| `TargetPartBytes` | 18 GiB (`19 327 352 832`) | runbook §20.1 (coerente com o default `-MaxPSTSizeMB = 18432` de §16.3, que é 18 GiB) |
| `HardPartBytes` | 20 GB (`20 000 000 000`) | runbook §20.1 ("considerando a convenção documentada pelo destino") |

**Leitura das unidades (decisão explícita, não arbitrária):** o runbook escreve `GiB` para o alvo e `GB`
para o limite duro no mesmo parágrafo. `PartitionPolicy` segue essa distinção literalmente — `GiB = 1024³`,
`GB = 10⁹`. A leitura decimal do limite duro é também a **mais conservadora** das duas possíveis
(20·10⁹ &lt; 20·1024³), portanto fail-closed em caso de dúvida. Ambos os valores são configuráveis por
implantação (`PstPartitionPlanning:TargetPartBytes`/`HardPartBytes`) e qualquer alteração **muda o
fingerprint da política**, logo muda a identidade de todo plano futuro (nunca reaproveita plano antigo).

As demais regras de §20.1 (preservar pasta inteira, particionar pasta grande por data, bin packing estável,
ordem por `folderPathNormalized + receivedUtc + stableItemFingerprint`) **exigem inventário de itens/pastas**
que nenhuma engine aceita por ADR fornece hoje — ver [Estado honesto](#estado-honesto-o-que-este-passo-pode-e-o-que-não-pode-derivar).

## Identidade determinística do plano

O runbook §20.2 define
`planHash = SHA256(sourceSha256 + canonicalJson(partitionPolicy) + pstEngineName + pstEngineVersion + plannerVersion)`.

`PartitionPlanIdentity.ComputePlanHash` preserva essa fórmula e a **estende** com o escopo e a identidade do
que foi efetivamente inspecionado, como exige o work order §4:

```text
planHash = SHA256(
  "archivebridge.pst.partition-plan.v1" ⋮ tenantId ⋮ projectId ⋮ artifactId ⋮
  (inspectionId | "none") ⋮ sourceSha256 ⋮ canonicalJson(partitionPolicy) ⋮
  (engineName | "none") ⋮ (engineVersion | "none") ⋮ plannerName ⋮ plannerVersion)
```

(`⋮` = separador de unidade U+001F de `DeterministicHash`, que não ocorre em nenhum componente.)

Sem a extensão de escopo, dois artefatos de **tenants diferentes** com conteúdo idêntico compartilhariam
identidade de plano — inaceitável sob isolamento por tenant/projeto (provado por
`TwoArtifactsWithIdenticalContentInDifferentProjectsNeverShareAPlanIdentity`).

Desfecho e motivo **não** entram no hash: são função determinística das entradas, e mantê-los fora garante
que o índice único de canonicidade signifique exatamente "mesmas entradas".

`part_key` é opaco e derivado (`SHA256("archivebridge.pst.partition-part.v1" ⋮ planHash ⋮ sequence)`) —
nunca UPN, caminho ou nome de arquivo (runbook §20.1: "nomes não incluem UPN completo; usar IDs opacos").

## Modelo de estados

```text
                       ┌──────────────────────────────────────────────┐
  custódia + inspeção  │ existe inspeção canônica p/ o hash registrado?│
        canônica       └───────────────┬──────────────────┬───────────┘
                                    não│                  │sim
                                       ▼                  ▼
                          Blocked / CanonicalInspectionUnavailable
                                                          │
                              hash da inspeção == hash de custódia?
                                       não│               │sim
                                          ▼               ▼
                             Blocked / SourceHashDivergence
                                                          │
                                        diagnóstico estrutural == Valid?
                                       não│               │sim
                                          ▼               ▼
                        Blocked / StructuralDiagnosticNotValid
                                                          │
                                              tamanho observado disponível (> 0)?
                                       não│               │sim
                                          ▼               ▼
                            Blocked / ObservedSizeUnavailable
                                                          │
                                            tamanho ≤ TargetPartBytes?
                                       não│               │sim
                                          ▼               ▼
              Unsupported / ItemInventoryUnavailable   Planned / SinglePartWithinTarget
                     (ZERO partes — nada inventado)     (1 parte cobrindo a origem inteira)
```

| Desfecho | Canônico? | Partes | Significado |
| --- | --- | --- | --- |
| `Planned` (0) | **sim** | ≥ 1 | plano reaproveitável em réplay idempotente |
| `Unsupported` (1) | não | 0 | pré-condições OK, mas falta inventário para derivar boundaries |
| `Blocked` (2) | não | 0 | pré-condição de custódia/inspeção não satisfeita (fail-closed) |

`Unsupported`/`Blocked` são **evidência append-only**: cada avaliação grava sua própria linha auditável,
nunca ocupa o índice único de canônicos e nunca é reaproveitada como se fosse um plano válido — exatamente o
tratamento que `ReadError`/`Stale`/`LimitExceeded` recebem no Passo 1.

## Estado honesto: o que este Passo pode e o que NÃO pode derivar

A inspeção canônica do Passo 1 observa hash, tamanho, diagnóstico estrutural e variante de formato.
`ItemCount`/`FolderCount` são sempre `null` (a engine header-only nunca percorre a árvore NDB; nenhuma engine
de contagem foi aceita por ADR — ver [decisão de adapter do Passo 1](vertical-slice-04b-pst-inspection.md#decisão-de-adapter-passo-1)).

Disso decorre exatamente **um** caso planejável sem inventário: quando o artefato inteiro cabe dentro do
`TargetPartBytes`, o plano é uma única parte que cobre a origem inteira — nenhum boundary de item/pasta
precisa ser derivado porque **nenhum split é necessário**.

Acima do alvo, agrupar por pasta/data ou fazer bin packing estável exigiria o inventário que não existe. O
plano então registra `Unsupported / ItemInventoryUnavailable` com **zero partes**, preservando honestamente o
tamanho observado. Nenhuma contagem, offset, boundary ou partição executável é fabricada
(`AnArtifactAboveTheTargetIsUnsupportedAndPersistsNoPartsAtAll`).

## Arquitetura do slice

```text
Application.PstProcessing
  PlanPstPartitionUseCase            ← NÃO recebe IPstEngine (read-only por construção)
     │
     ├── IPstCustodyStore.FindAsync(scope, artifact)          ─── anti-IDOR: NotFound indistinguível
     ├── IPstInspectionStore.FindCanonicalAsync(..., hashDeCustódia) ─ staleness fail-closed
     ├── IPartitionPlanner.Plan(custody, inspection, ...)     ─── função PURA, sem I/O
     └── IPartitionPlanStore.FindCanonicalAsync/SaveAsync     ─── réplay idempotente + append-only
              │
              ▼
Infrastructure.PstProcessing (adapters substituíveis)
  SizeBoundedPartitionPlanner  ── delega à regra pura do Domain (PartitionPlanning), sem vendor
  SqlPartitionPlanStore        ── SQL Server, RLS + filtro project_id, plano+partes na MESMA transação

Domain.PstProcessing
  PartitionPolicy · PartitionPlanIdentity · PartitionPlanning (regra pura)
  PartitionPlan · PartitionPlanPart · PartitionPlanOutcome · PartitionPlanReason
```

- **Substituibilidade**: `IPartitionPlanner` é a fronteira. Quando uma engine primária com inventário real
  for aceita por ADR, um planner semântico (runbook §20.4) implementa a MESMA porta e passa a produzir
  múltiplas partes — sem tocar Domain/Application/Contracts.
- Domain/Contracts/Application permanecem independentes de parser/fornecedor (`VendorBoundaryTests`,
  `DependencyRuleTests`).
- A assinatura de `IPartitionPlanner.Plan` é **síncrona e sem `CancellationToken`**: um sinal de tipo de que
  planejar não faz I/O nem tem efeito colateral.

## Modelo de dados (migration `0021_slice4b_partition_planning.sql`, aditiva)

- **`dbo.pst_partition_plans`** — uma linha por AVALIAÇÃO (append-only). Guarda identidade determinística
  (`plan_hash`), fingerprint e limites da política, nome/versão de planner e engine, desfecho/motivo
  sanitizados, `part_count`, correlação e timestamp. FK composta ao artefato **e à inspeção** amarra o plano
  ao mesmo tenant/projeto/artefato no banco (a constraint `UQ_pst_inspections_scope`, adicionada de forma
  aditiva por esta migration, é o que torna esse FK possível).
- **`dbo.pst_partition_plan_parts`** — partes planejadas, com `part_key` opaco, sequência contígua e tamanho
  planejado. FK composta ao plano dentro do mesmo escopo.
- **Canonicidade**: coluna `is_canonical` gravada pela Application com o mesmo valor que
  `PartitionPlan.IsCanonical` calcula no Domain, travada por `CK_pst_partition_plans_is_canonical`
  (mesmo desenho já validado na migration 0020 — o SQL Server proíbe coluna computada no predicado de índice
  filtrado). O índice único **filtrado** `UX_pst_partition_plans_canonical`
  (`tenant_id, project_id, artifact_id, plan_hash WHERE is_canonical = 1`) é o backstop de corrida.
- **Coerência travada no banco**: `CK_pst_partition_plans_outcome_reason` reforça que o desfecho é sempre a
  derivação canônica do motivo; `CK_pst_partition_plans_outcome_fields` reforça que só um plano concluído tem
  inspeção/tamanho/engine/partes e que qualquer outro desfecho tem **zero** partes.
- Ambas as tabelas participam de `rls.tenant_isolation_policy` (FILTER + BLOCK AFTER INSERT) e concedem
  apenas `SELECT, INSERT` a `ab_app_role` (append-only; manutenção não recebe grant algum).
- Migrations 0001–0020 permanecem **byte-for-byte** intactas (`MigrationHashTests`).

## Idempotência, concorrência e invalidação

- Mesmas entradas + mesma policy/config ⇒ mesmo `plan_hash` ⇒ o plano canônico já persistido é devolvido
  **sem gravar nova linha** (`IdempotentReplayReturnsTheSameCanonicalPlanWithoutANewRow`).
- Mudança de hash de origem **ou** de policy/config ⇒ novo `plan_hash` ⇒ nunca reaproveita o plano anterior
  (`ChangingThePolicyProducesANewIdentityAndNeverReusesThePreviousPlan`).
- Origem alterada após o registro ⇒ a inspeção vira `Stale`, não existe canônico para o hash registrado e o
  planejamento falha fechado (`AnArtifactChangedAfterRegistrationCannotBePlanned`).
- Concorrência: 6 execuções simultâneas convergem para **uma** linha canônica
  (`ConcurrentPlanningOfTheSameArtifactConvergesToExactlyOneCanonicalPlan`); o perdedor da corrida relê o
  canônico. Se a releitura não encontrar nada, falha fechado com `PartitionPlanConflictUnresolvedException` —
  nunca devolve ao chamador um plano não persistido.
- Defesa em profundidade: a Application revalida `IsCanonical` (da inspeção **e** do plano) e a identidade
  determinística antes de reaproveitar qualquer coisa que a store devolva como "canônico".

## A persistência é uma fronteira NÃO confiável (AB-4B-005)

Um `CHECK` constraint do SQL Server é **row-local**: ele não consegue relacionar a linha do plano às suas
linhas-filhas. Por isso o banco aceita, sozinho, uma linha canônica cujas *partes* contradizem o agregado —
sequências não contíguas, soma diferente de `source_size_bytes`, parte acima de `hard_part_bytes`,
`covers_entire_source` incoerente com o caso "cabe em uma parte". Reidratar essas linhas sem validá-las
transformaria `PartitionPlan.Rehydrate` num caminho de confiança implícita e anularia, no réplay, os
invariantes que `PartitionPlan.Create` garante na criação.

- **Caminho único de validação**: `Create` e `Rehydrate` compartilham `FindStructuralViolation` — criar e
  reidratar nunca podem divergir sobre o que é um plano válido. A diferença é apenas o tipo de falha:
  `ArgumentException` (argumento inválido) vs. `PartitionPlanIntegrityViolationException` (dado persistido
  corrompido/adulterado).
- **Identidade revalidada contra as próprias entradas persistidas**: comparar o `plan_hash` gravado com o
  `plan_hash` PEDIDO pelo chamador não prova nada sobre a linha. `Rehydrate` recalcula
  `PartitionPlanIdentity.ComputePlanHash(...)` a partir de tenant/projeto/origem/política/planner
  **persistidos** e exige igualdade exata; cada `part_key` tem de ser exatamente
  `ComputePartKey(plan_hash, sequência)`. `PartitionPlan.HasConsistentIdentity()` expõe a mesma verificação
  para a Application aplicá-la a qualquer implementação de `IPartitionPlanStore`.
- **Fail-closed, nunca normalização**: uma linha inválida não é corrigida, truncada nem ignorada — a leitura
  falha e nenhum plano é devolvido ou reaproveitado (`ReplayOf...FailsClosed`, cinco cenários com dados
  corrompidos gravados fora da aplicação em SQL Server real).
- **Sem migration nova**: os invariantes restantes são **agregados** (plano × partes) e nenhum `CHECK`
  row-local os expressa; os row-local que existem já estão em 0021 (`part_sequence >= 1`,
  `planned_size_bytes >= 0`, coerência desfecho/motivo/`part_count`/`is_canonical`). A migration 0021
  permanece **byte-for-byte** intacta, como toda 0001–0020.

## Segurança e minimização de PII

- Tenant/projeto/artefato vêm sempre do `TenantScope` resolvido pelo composition root; cross-tenant e
  cross-project retornam `PstArtifactNotFoundException` indistinguível de "não existe", e o plano não é
  legível de outro escopo mesmo conhecendo sua identidade exata
  (`CrossTenantAndCrossProjectPlanningIsDeniedIndistinguishablyFromNotFound`).
- Nenhuma coluna das tabelas novas guarda caminho, nome de arquivo, UPN, assunto, corpo, destinatário ou
  anexo — provado lendo TODAS as colunas persistidas do plano e das partes
  (`ThePersistedPlanCarriesNoPathFileNameOrMailboxIdentifier`).
- O PST permanece byte-for-byte e nenhum arquivo novo é criado
  (`ArtifactWithinTargetIsPlannedPersistedAndLeavesThePstByteForByteUntouched`); os checkpoints do Passo 1
  não recebem nenhuma escrita (`PlanningNeverWritesToTheCustodyOrInspectionCheckpointsOfStepOne`).

## Operação (runbook do Passo 2)

- Capacidade **desabilitada por padrão**: `PstPartitionPlanning:Enabled=false`. Habilitar exige
  `PstInspection:Enabled=true` — planejar sem inspeção é configuração incoerente e derruba o host em vez de
  ser silenciosamente ignorada.
- Limites inválidos (não positivos, alvo acima do duro) derrubam o startup; nunca são "corrigidos".
- Alterar `TargetPartBytes`/`HardPartBytes` é uma decisão auditável: muda o fingerprint da política e,
  portanto, a identidade de todos os planos calculados depois disso. Planos anteriores permanecem legíveis
  (append-only) e continuam resolvendo pela sua própria identidade.
- Diagnóstico operacional: `outcome`/`reason` explicam por que um artefato não foi planejado, sem nenhum dado
  sensível. `Unsupported / ItemInventoryUnavailable` é o sinal de que aquele PST só poderá ser particionado
  quando uma engine com inventário real for aceita por ADR — não é falha de execução.
- Nenhum worker/fila é registrado: `PlanPstPartitionUseCase` é diretamente invocável (testes e composição
  futura), como no Passo 1.

## Critérios de aceite (mapeamento para AB-4B-004)

| # | Critério | Onde é provado |
| --- | --- | --- |
| 1 | Plano só nasce de inspeção canônica válida + hash coerente | `PlanningAnArtifactThatWasNeverInspectedIsBlocked`, `AnArtifactChangedAfterRegistrationCannotBePlanned`, `AStructurallyInvalidPstIsBlockedEvenWithACanonicalInspection`, `Slice4bPartitionPlanningDomainTests.*Blocked*` |
| 2 | Planejamento read-only; PST byte-for-byte inalterado | `ArtifactWithinTargetIsPlannedPersistedAndLeavesThePstByteForByteUntouched`, `PlanningNeverWritesToTheCustodyOrInspectionCheckpointsOfStepOne`, `PlanningHasNoPathToThePstEngineOrAnyOtherWriteCapableDependency` |
| 3 | Mesmas entradas/configuração ⇒ mesmo plano canônico, sem efeitos duplicados | `IdempotentReplayReturnsTheSameCanonicalPlanWithoutANewRow`, `ExistingCanonicalPlanIsReplayedWithoutPersistingAgain` |
| 4 | Mudança de hash de origem ou policy/config invalida o réplay anterior | `ChangingThePolicyProducesANewIdentityAndNeverReusesThePreviousPlan`, `ChangingThePolicyChangesThePlanIdentitySoNoStalePlanIsEverReused`, `PlanHashChangesWithEveryIdentityComponent` |
| 5 | Concorrência converge deterministicamente, sem múltiplos canônicos | `ConcurrentPlanningOfTheSameArtifactConvergesToExactlyOneCanonicalPlan`, `AWriteConflictIsResolvedByRereadingTheCanonicalPlan` |
| 6 | Falta de informação vira estado explícito, nunca valores inventados | `AnArtifactAboveTheTargetIsUnsupportedAndPersistsNoPartsAtAll`, `ArtifactAboveTheTargetIsUnsupportedWithNoPartsAndNoInventedCounts`, `ANonPlannedOutcomeCanNeverCarryParts` |
| 7 | Domain/Application sem dependência de parser/vendor | `VendorBoundaryTests`, `DependencyRuleTests` (inalterados, verdes) |
| 8 | Cross-tenant/cross-project negados sem disclosure | `CrossTenantAndCrossProjectPlanningIsDeniedIndistinguishablyFromNotFound`, `CrossTenantAndCrossProjectAreIndistinguishableFromNotFound` |
| 9 | Nenhuma capacidade STOP-THE-LINE implementada/invocada | ver [Fora do escopo](#fora-do-escopo--stop-the-line) |
| 10 | Migrations anteriores intactas; 0021 aditiva; CI verde | `MigrationHashTests.Migration0021AppliesCleanlyAndPriorHashesRemainStable` + gates do `.github/workflows/ci.yml` (inalterado) |

## Fora do escopo — STOP-THE-LINE

Nenhum código deste Passo implementa ou invoca: execução de split/rewrite/compactação/repair de PST; criação
de PST de saída ou cópia de conteúdo de mensagens; parser/vendor não aprovado por ADR; Export-EVArchive real;
AzCopy, Azure staging ou upload; Purview, Graph, Exchange Online ou import job; validação/reconciliação
pós-partição; CSV builder de importação; reconciliação final no Microsoft 365; avanço para o próximo Passo.

## Limitações residuais (para Passos futuros)

- Só o caso "cabe em uma parte" é planejável hoje. PSTs acima do alvo ficam em
  `Unsupported / ItemInventoryUnavailable` até que um ADR aceite uma engine com inventário real de
  itens/pastas — a partir daí um planner semântico (runbook §20.4) implementa `IPartitionPlanner` e o modelo
  de dados já comporta múltiplas partes.
- Avaliações não canônicas (`Unsupported`/`Blocked`) acumulam linhas de evidência a cada execução, por
  desenho append-only (mesmo comportamento de `ReadError`/`Stale` no Passo 1). Uma política de retenção de
  evidência é decisão de slice futuro.
- `PartitionPlanPart` ainda não modela boundaries de item/pasta (offsets, faixas de data, fingerprints):
  seriam campos sem significado enquanto não houver inventário. Serão adicionados junto do planner que
  realmente os derive.
- Nenhuma orquestração assíncrona (fila/worker) e nenhum endpoint HTTP/Portal foram adicionados — fora do
  escopo obrigatório deste Passo.

## Regra de encerramento

Este PR permanece **Draft** durante toda a implementação. Não marcar Ready nem fazer merge sem
`ARCHIVEBRIDGE_MERGE_APPROVED` para o HEAD corrente do Engineering Reviewer, com CI totalmente verde.
