# Vertical Slice 4B — PST Inspection & Inventory (Passo 1)

## Status

**Em desenvolvimento — PR deve permanecer em Draft até `ARCHIVEBRIDGE_MERGE_APPROVED` explícito do
Engineering Reviewer para o HEAD SHA corrente, com CI totalmente verde.**

Work order versionado: [`docs/engineering/requests/AB-4B-001.md`](requests/AB-4B-001.md) (`REQUEST_ID: AB-4B-001`).

## Objetivo

Iniciar a fundação documentada de **PST Inspector / Inventory** após o fechamento do Slice 4A: contratos de
Domain/Application, modelo de domínio, persistência/checkpoint e a primeira execução determinística de
inspeção **read-only** sobre PSTs já sob custódia autorizada — sem split, repair, staging ou importação
(ver [Fora do escopo](#fora-do-escopo--stop-the-line)).

## Decisão de adapter (Passo 1)

> Esta seção documenta uma decisão de implementação tomada dentro do escopo autorizado do work order
> (§"decisões de adapter necessárias"). Não é um ADR aceito — fica sujeita à revisão do Engineering
> Reviewer, que pode solicitar ajuste antes do merge.

**Nenhum ADR aceito até este Passo autoriza uma engine primária de leitura completa de PST.**
[ADR-0004](../adr/0004-aspose-email-engine-primaria.md) (Aspose.Email como engine primária) foi
**substituído** pelo [ADR-0013](../adr/0013-exportacao-ev-multiversao.md) — o Enterprise Vault passa a
extrair/segmentar PSTs na origem e o Aspose saiu do caminho crítico; a ingestão de PSTs pré-existentes
(§17 do runbook) ficou com **decisão adiada para novo ADR**. [ADR-0005](../adr/0005-libpff-validador-independente.md)
(libpff como validador independente) permanece **`BLOCKED_PENDING_EVIDENCE`** (parecer jurídico LGPL
pendente) — capacidade opcional, fora do MVP.

Coerente com "não antecipe capacidades posteriores" (fonte de autoridade do work order), o Passo 1
implementa um **inspetor estrutural somente de cabeçalho** (`HeaderOnlyPstInspectionEngine`), sem
dependência de biblioteca de fornecedor:

- calcula SHA-256/tamanho reais dos bytes lidos (streaming, mesmo padrão do runbook §17.2);
- classifica ANSI/Unicode/versão pelos 12 bytes iniciais do cabeçalho `[MS-PST]` (`dwMagic`,
  `wMagicClient`, `wVer`) — valores públicos e estáveis da especificação, sem invocar parser de terceiro;
- **nunca** percorre a árvore NDB — `ItemCount`/`FolderCount` permanecem sempre `null` em
  `PstInspectionResult` (nenhuma contagem é inventada; nenhuma engine de contagem foi aceita em ADR até
  este Passo);
- é substituível: `IPstEngine` é a única fronteira que Application/Domain enxergam. Quando uma engine
  primária real for aceita por ADR futuro (ex.: libpff promovido de `BLOCKED_PENDING_EVIDENCE`, ou uma
  nova decisão substituindo o adiamento do ADR-0004), ela implementa `IPstEngine` sem tocar
  Domain/Application/Contracts.

Esta escolha satisfaz o critério de aceite 4 do work order ("contagens/inventário quando suportados pelo
mecanismo escolhido") ao **não fingir suporte que a engine não tem**, em vez de adiantar uma dependência de
parser ainda não autorizada.

## Arquitetura do slice

```text
Application.PstProcessing
  InspectPstArtifactUseCase
     │
     ├── IPstCustodyStore.FindAsync(scope, artifact)   ─── anti-IDOR: NotFound indistinguível
     ├── IPstInspectionStore.FindCanonicalAsync(...)    ─── réplay idempotente (sem reinvocar a engine)
     └── IPstEngine.InspectAsync(scope, artifact)       ─── só quando não há canônico
              │
              ▼
Infrastructure.PstProcessing (adapters substituíveis)
  SqlPstCustodyStore / SqlPstInspectionStore  ── SQL Server, RLS + filtro project_id, append-only
  HeaderOnlyPstInspectionEngine               ── FileStream read-only + ArtifactPathContainment
                                                  (raiz configurada, symlink/reparse rejeitado)
```

- Domain/Contracts permanecem independentes de qualquer biblioteca de parsing de PST (reforçado por
  `VendorBoundaryTests`).
- `IPstCustodyStore`/`IPstInspectionStore` seguem o mesmo padrão de custódia SQL já usado pelo Slice 4A
  (`mapping_validation_attempts`/`mapping_validation_issues`): append-only, RLS + filtro `project_id`
  explícito, identidade da aplicação (nunca manutenção).
- `HeaderOnlyPstInspectionEngine` reutiliza `ArtifactPathContainment` (já usado por
  `FileSystemMappingArtifactStore`) para canonicalização/contenção e rejeição de symlink/reparse point
  antes de abrir qualquer arquivo.

## Modelo de dados (migration `0020_slice4b_pst_inspection.sql`, aditiva)

- **`dbo.pst_artifacts`** — registro de custódia (imutável, nunca `UPDATE`): identidade opaca
  (`artifact_id`), escopo tenant/projeto, caminho relativo à raiz configurada, hash/tamanho observados no
  registro (baseline de staleness). `UQ_pst_artifacts_path` impede registrar o mesmo caminho duas vezes no
  mesmo escopo.
- **`dbo.pst_inspections`** — checkpoint append-only de cada TENTATIVA de inspeção. Só uma tentativa
  `Completed` cujo hash observado bate com o `expected_hash` é elegível a canônica. Isto é reforçado por uma
  coluna `is_canonical` gravada pela Application com o mesmo valor que `PstInspectionRecord.IsCanonical`
  calcula no Domain (não um filtro de índice que reimplemente a regra separadamente e possa divergir dela) —
  NÃO é uma coluna computada porque o SQL Server proíbe referenciar coluna computada no predicado de um
  índice filtrado; em vez disso, um CHECK constraint (`CK_pst_inspections_is_canonical`) trava no banco que o
  valor gravado é sempre consistente com `outcome`/`observed_hash`/`expected_hash` — e pelo índice único
  **filtrado** `UX_pst_inspections_canonical` (`WHERE is_canonical = 1`) sobre essa coluna, que é o backstop
  de corrida além da revalidação em `Application` (que também nunca confia cegamente no que
  `FindCanonicalAsync` devolve — revalida `IsCanonical` antes de reaproveitar). `ReadError`/`Stale`/
  `LimitExceeded` (`is_canonical = 0`) nunca ocupam nem disputam este índice — múltiplas tentativas
  não-canônicas do mesmo artefato coexistem livremente como evidência (ver
  `ReadErrorIsNeverCanonicalAndDoesNotBlockASubsequentSuccessfulInspection`,
  `ConcurrentReadErrorAttemptsAreNotConfusedWithACanonicalRace`). Duas execuções concorrentes do mesmo
  artefato convergem para exatamente UMA linha canônica (ver
  `ConcurrentInspectionOfTheSameArtifactConvergesToExactlyOneCanonicalRecord`). `SqlPstInspectionStore.SaveAsync`
  usa `OUTPUT inserted.*` para devolver ao chamador exatamente a linha persistida (inclusive timestamps na
  precisão de milissegundo de `DATETIME2(3)`) — nunca um valor em memória que divergiria do que um réplay
  subsequente leria de volta.
- Ambas as tabelas participam da política de RLS existente (`rls.tenant_isolation_policy`) e concedem
  apenas `SELECT, INSERT` a `ab_app_role` (nenhum `UPDATE`/`DELETE` — append-only; a identidade de
  manutenção não recebe grant algum).

## Escopo funcional implementado

1. Contratos `IPstCustodyStore`, `IPstInspectionStore`, `IPstEngine` (Contracts/Application) e modelo de
   domínio (`MigrationArtifact`, `PstInspectionRecord`, `PstRelativePath`, `PstInspectionOutcome`,
   `PstStructuralDiagnostic`, `PstFormatVariant`).
2. `InspectPstArtifactUseCase`: resolve custódia server-side, reaproveita resultado canônico existente
   (idempotência, "sem duplicar efeitos"), invoca a engine só quando necessário, classifica o resultado em
   `Completed`/`Stale`/`LimitExceeded` e trata corrida de gravação relendo o canônico.
3. `HeaderOnlyPstInspectionEngine`: leitura read-only, streaming SHA-256, diagnóstico estrutural sanitizado
   (`Valid`/`TooSmall`/`InvalidSignature`/`InvalidClientSignature`/`UnsupportedVersion`/`ReadError`),
   limite de tamanho e timeout configuráveis fail-closed.
4. Persistência SQL (`SqlPstCustodyStore`, `SqlPstInspectionStore`) com migration aditiva `0020`.
5. Composition root fail-closed em `ArchiveBridge.Workers.Pst` (`PstInspectionComposition`,
   `PstInspectionOptions`, padrão `Enabled=false` por padrão) — registra apenas o caso de uso diretamente
   invocável; **não** registra um worker que reivindica fila (ver [Fora do escopo](#fora-do-escopo--stop-the-line)).

## Requisitos não funcionais

### Segurança e custódia

- Path/tenant/projeto nunca vêm do cliente: `TenantScope` é resolvido pelo composition root a partir do
  principal autenticado; o caminho físico é sempre `raiz configurada + relative_path` validado por
  `ArtifactPathContainment` (canonicalização + rejeição de symlink/reparse point) antes de abrir o arquivo, E
  revalidado uma SEGUNDA vez imediatamente após a abertura e antes de qualquer leitura — estreitando (não
  eliminando; ver limitação abaixo) a janela TOCTOU entre a checagem e a abertura.
- Cross-tenant/cross-project retorna `PstArtifactNotFoundException` indistinguível de "não existe"
  (`IPstCustodyStore.FindAsync` filtra por `project_id` sob RLS).
- Erros de leitura (permissão, I/O, TOCTOU) nunca vazam stack trace/caminho real — viram
  `PstStructuralDiagnostic.ReadError` sanitizado.
- O PST é preservado byte-for-byte: a engine abre `FileMode.Open`/`FileAccess.Read`/`FileShare.Read`,
  nunca escreve, nunca repara, nunca faz split.

### Idempotência e concorrência

- Mesmo artefato + mesmo hash de custódia ⇒ resultado canônico reaproveitado sem reinvocar a engine.
- Artefato alterado (hash observado ≠ hash registrado) ⇒ `Stale`, fail-closed, nunca reutiliza resultado
  anterior; cada divergência é sua própria linha de evidência (não colide no índice canônico, que só
  protege `Completed`).
- Corrida de gravação do canônico ⇒ `PstInspectionConflictException` no `SaveAsync`; a Application relê o
  canônico já persistido em vez de tratar como erro de negócio. Se a releitura pós-conflito não encontrar
  nenhum canônico (não deveria ser possível sob o invariante — quem venceu a corrida DEVERIA estar lá), a
  Application falha fechado com `PstInspectionConflictUnresolvedException` em vez de devolver ao chamador um
  registro que nunca foi persistido. A tradução de `SqlException` (2601/2627) para `PstInspectionConflictException`
  em `SqlPstInspectionStore` é restrita à violação do índice `UX_pst_inspections_canonical` especificamente —
  outra violação de UNIQUE/PK nunca é mascarada como corrida idempotente.

### Observabilidade/auditoria

- `pst_inspections` guarda `engine_name`/`engine_version`/`correlation_id`/timestamps por tentativa —
  nunca conteúdo de mensagem, assunto, corpo, destinatário ou anexo (minimização de PII; ver §11 do
  [overview de engenharia](overview.md)).

## Critérios de aceite (mapeamento para AB-4B-001)

| # | Critério | Onde é provado |
| --- | --- | --- |
| 1 | Domain/Application independentes de parser/vendor | `VendorBoundaryTests`, `DependencyRuleTests` |
| 2 | PST autorizado inspecionado read-only, resultado persistido/scoped/auditável | `Slice4bPstInspectionTests.ValidUnicodePstIsDiagnosedValidAndBecomesCanonical` |
| 3 | Hash/tamanho representam os bytes realmente inspecionados; PST byte-for-byte inalterado | mesmo teste (`onDisk == bytes` após inspeção) |
| 4 | Reexecução idempotente ⇒ canônico sem duplicar efeitos | `IdempotentReplayReturnsTheSameCanonicalRecordWithoutANewRow`, `ReadErrorIsNeverCanonicalAndDoesNotBlockASubsequentSuccessfulInspection`, `ConcurrentReadErrorAttemptsAreNotConfusedWithACanonicalRace` |
| 5 | Artefato alterado/stale falha fechado, não reutiliza resultado anterior | `HashDivergenceSinceRegistrationFailsClosedAsStale` |
| 6 | Arquivo inválido/truncado/corrompido ⇒ diagnóstico estruturado, nunca sucesso falso/crash | `TruncatedFileIsDiagnosedTooSmallNeverThrows`, `InvalidSignatureFileIsDiagnosedStructurallyNeverThrows`, `UnsupportedVersionIsDiagnosedStructurally` |
| 7 | Cross-tenant/cross-project/path traversal negados sem revelar existência | `CrossTenantAndCrossProjectAreDeniedIndistinguishablyFromNotFound`, `Slice4bPstInspectionDomainTests.RelativePath*` |
| 8 | Nenhuma capacidade do STOP-THE-LINE chamada/implementada | ver [Fora do escopo](#fora-do-escopo--stop-the-line) — nenhum código deste PR toca Export-EVArchive/partition/Outlook/AzCopy/Purview/Graph/EXO/import |
| 9 | Migrations anteriores (0001–0019) inalteradas; nova migration aditiva/determinística | `MigrationHashTests` (gate de CI, não alterado) + `0020_slice4b_pst_inspection.sql` só adiciona |
| 10 | CI completo verde | gates do `.github/workflows/ci.yml`, sem alteração |

Concorrência (critério transversal do work order) é coberta por
`ConcurrentInspectionOfTheSameArtifactConvergesToExactlyOneCanonicalRecord`.

## Fora do escopo — STOP-THE-LINE

Nenhum código deste Passo implementa ou invoca: Export-EVArchive/exportação real do Enterprise Vault;
split/partition execution ou repair de PST; Outlook automation; upload/AzCopy ou Azure staging; Purview,
Graph, Exchange Online ou import job; reconciliação final no Microsoft 365; conteúdo real de mailbox em
logs/evidências; avanço para o próximo Passo do Slice 4B.

## Limitações residuais (para Passos futuros)

- A dupla checagem de contenção/reparse (`ArtifactPathContainment`, antes e depois da abertura do arquivo)
  **estreita** a janela TOCTOU entre a checagem e a abertura, mas não a **elimina**: ambas reexaminam o
  caminho no sistema de arquivos, não o handle/descritor já aberto. Uma garantia atômica exigiria
  verificação baseada em handle (API específica de plataforma via P/Invoke), fora do escopo deste Passo sem
  novo ADR — ver `docs/security/threat-model-slice-04b.md`.
- `ItemCount`/`FolderCount` permanecem `null` — travessia da árvore NDB exige uma engine primária ainda
  não aceita por ADR (ver [Decisão de adapter](#decisão-de-adapter-passo-1)).
- Nenhuma orquestração assíncrona (fila/worker) foi registrada — `InspectPstArtifactUseCase` é hoje
  diretamente invocável (por testes e por composição futura), não reivindicado por um worker em produção.
- Nenhum endpoint HTTP/Portal foi adicionado — fora do escopo obrigatório deste Passo (work order §1-13
  não menciona superfície de Control Plane para inspeção).
- Registro de custódia (`RegisterAsync`) não tem um fluxo de autorização/UI formal neste Passo — o work
  order pressupõe PSTs "já sob custódia autorizada"; o mecanismo de autorização da custódia em si é
  decisão de Passo/slice futuro.

## Regra de encerramento

Este PR permanece **Draft** durante toda a implementação. Não marcar Ready nem fazer merge sem
`ARCHIVEBRIDGE_MERGE_APPROVED` para o HEAD corrente do Engineering Reviewer, com CI totalmente verde.
