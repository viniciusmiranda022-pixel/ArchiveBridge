# Threat model — I6/EPIC-07, Passo 1 (Purview Service Result Importer & Evidence Foundation)

Delta sobre o modelo de ameaças da plataforma (mesmo formato dos deltas de Slice 5, incorporados abaixo do
capítulo anterior em [`threat-model-slice-05.md`](threat-model-slice-05.md)). Escopo: fundação de ingestão e
custódia dos resultados do serviço Purview gerados pelo workflow humano já definido no runbook (§25.9/§26) —
planejamento determinístico/server-side do nome do import job, registro de observações do provider
transcritas pelo operador, importação bounded/fail-closed do validation report / service result, correlação
1:1 com a cadeia canônica já aceita (`WaveEntry ↔ Binding ↔ PartitionExecution ↔ Upload manifest ↔ Mapping`)
e uma primeira avaliação de completude da evidência do provider — **sem** automação do portal Purview, sem
criação/validação/início automático de import job, sem clique/execução automática de `Import data`, sem
scraping/browser automation, sem Graph/EXO writes, sem coleta real automatizada de estatísticas EXO
post-import, sem cálculo final expected-vs-observed, sem disposition de exceções, sem certificate, sem
`COMPLETED` de wave/projeto e sem I7 Hardening (STOP-THE-LINE de
[`docs/engineering/requests/AB-I6-001.md`](../engineering/requests/AB-I6-001.md)). Nenhum destes fluxos
existe no código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.

## Ativos adicionais

- **Planos de import job** (`purview_import_job_plans`): nome planejado determinístico/server-side
  (`ab-imp-<hash>-<tentativa>`, alfabeto restrito a minúsculas/dígitos/hífen/underscore exigido pelo portal),
  a impressão digital da evidência canônica que o autorizou e responsável/data. Nunca contém o conteúdo do
  mapping, PII ou segredo — apenas metadado de planejamento.
- **Vínculo plano→provider** (`purview_import_job_provider_bindings`): no máximo UMA linha por plano,
  amarrando-o ao `provider_operation_id` observado na PRIMEIRA vez. Evidência de INTEGRIDADE de identidade,
  não conteúdo — existe apenas para impedir reassociação silenciosa (item 5 do work order).
- **Observações do import job** (`purview_import_job_observations`): progressão transcrita manualmente pelo
  operador (nome/ID do job, horário observado, status observado, identificador do operador). Evidência
  OPERACIONAL append-only — nunca screenshot/relatório bruto, nunca decide sozinha o fechamento da onda.
- **Versões do service result report** (`purview_service_result_report_versions`): conteúdo bruto do
  validation report / service result anexado pelo operador (hostil, bounded, hashado), metadados de
  custódia (tamanho, contagem de linhas, contagem total autodeclarada) e responsável.
- **Linhas normalizadas do service result** (`purview_service_result_rows`): identidade de PST por nome
  remoto determinístico, status normalizado e contadores agregados (importado/ignorado/corrompido) — cada
  contador é `NULL` (Unknown/NotReported) quando o serviço não forneceu o campo, nunca `0`. Nenhuma linha
  carrega mailbox/UPN, caminho local, conteúdo de mensagem ou segredo.

## Classificação de dados

Nenhuma das cinco tabelas novas é "zero PII" no sentido absoluto — `operator_label`/`created_by`/
`uploaded_by` são identificadores de operador (mesma classificação de campos equivalentes em
`MailboxPrecheckSnapshot`/`PurviewMappingCsvVersion`) — mas nenhuma delas contém SAS, credencial, token,
UPN de mailbox de destino, conteúdo de e-mail, transcript PowerShell bruto ou stack trace. O validation
report / service result é tratado como **entrada hostil não confiável** do início ao fim (item 6 do work
order): o parser (`PurviewServiceResultReportParser`) nunca executa conteúdo, nunca confia em nome/path/
extensão do chamador, aplica limites estritos de tamanho/linhas/campos/encoding e recusa qualquer coluna
fora de um pequeno conjunto fixo reconhecido — este NÃO é o formato interno (não documentado/certificado)
do Purview, é o esquema próprio do ArchiveBridge para o material que o operador transcreve/exporta do portal
(nenhuma API Purview não documentada é presumida ou invocada).

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| `provider_operation_id` observado tratado como chave lógica, permitindo reassociar silenciosamente a identidade do job | A chave lógica permanece SEMPRE `PurviewImportJobName` (determinístico/server-side, `PlanPurviewImportJobUseCase`); `provider_operation_id` é evidência OBSERVADA depois da criação humana (item 5). O vínculo plano→provider é "amarrado" na PRIMEIRA observação (`purview_import_job_provider_bindings`) e a Application/store recusam fail-closed (`PurviewImportJobIdentityConflictException`) tanto uma reassociação do MESMO plano a um provider ID diferente quanto o reaproveitamento do MESMO provider ID por um plano/onda diferente do escopo — o índice único `UQ_pijpb_provider_operation_id (tenant_id, project_id, provider_operation_id)` é o backstop no BANCO, não apenas na Application. |
| Nome planejado do import job gerado a partir de entrada do caller ou fora do alfabeto exigido pelo portal | `PurviewImportJobName.Compute` deriva o nome EXCLUSIVAMENTE de tenant/projeto/onda/sequência de tentativa já resolvidos server-side (hash determinístico, hexadecimal minúsculo + dígitos + hífen); o caller nunca fornece ou influencia o nome. `FromPersistedValue` revalida o alfabeto/tamanho na reidratação (fail-closed) — a persistência é fronteira não confiável. |
| Import job planejado/observado antes de a onda ter upload+mapping canônicos, ou depois de um drift real (novo binding/execução/upload sem regenerar o mapping) | `PurviewImportJobEvidenceGuard` reaproveita `ResolvePurviewMappingEvidenceUseCase` (Passo 4) para revalidar TODA a cadeia canônica no instante da chamada e recomputa a impressão digital fresca da evidência com o MESMO gerador puro do mapping (`PurviewMappingCsvGenerator.Generate`, sem I/O) — comparada contra o `Fingerprint` da versão `Usable` publicada. Qualquer divergência (mapping nunca publicado, ou publicado mas desatualizado em relação ao estado atual) recusa fail-closed (`PurviewImportJobPrerequisiteException`) o planejamento, o registro de observação E a importação do relatório — nenhuma das três operações aceita evidência obsoleta. |
| Wave/plano de outro tenant/projeto acessado por IDOR | Toda leitura (`GetPlanByNameAsync`, `GetLatestObservationAsync`, `GetByContentHashAsync`, `GetLatestAsync`, `GetRowsAsync`) participa de `rls.tenant_isolation_policy` (FILTER + BLOCK) e filtra `project_id`/`wave_id` explicitamente; um plano/relatório de outro escopo é indistinguível de inexistente — sempre `PurviewImportJobSourceNotFoundException`, nunca revela qual causa. |
| Validation report / service result malicioso ou malformado (oversized, encoding inválido, coluna desconhecida, campo em excesso, byte NUL, injeção de fórmula via valor numérico) aceito ou executado | `PurviewServiceResultReportParser` é PURO e bounded: limite de bytes (`MaxReportBytes`) e linhas (`MaxDataRows`) verificado ANTES de qualquer parsing; decodificação UTF-8 estrita (`throwOnInvalidBytes`); byte NUL recusado; cabeçalho restrito a um conjunto FIXO de colunas reconhecidas (coluna desconhecida recusa o relatório inteiro); contagem de campos por linha deve bater EXATAMENTE com o cabeçalho; identidade de PST validada contra o padrão determinístico exato (`p_<hex32>_part<NNN>.pst`) — nunca aceito como string arbitrária; um valor numérico malformado recusa o relatório inteiro (nunca é silenciosamente tratado como Unknown, que é reservado para AUSÊNCIA do campo). Nenhum conteúdo é interpretado como fórmula/comando — o parser só produz valores estruturados (enum/long?), nunca reemite texto do relatório em qualquer superfície executável. |
| Métrica ausente do serviço convertida em zero, mascarando uma falha real de importação | `PurviewServiceResultRow` usa `long?` para todo contador; o parser devolve `null` tanto quando a COLUNA inteira está ausente quanto quando uma célula específica está vazia — nunca `0`. Comprovado por `ParseLeavesAMissingColumnAsNullNeverAsZero`/`ParseLeavesAnEmptyCellInAnExistingColumnAsNullNeverAsZero` (Domain). |
| Linha do relatório associada a um PST por ordem/posição/nome de arquivo de planejamento, ou a um PST de outra onda/tenant | `PurviewServiceResultCorrelation.Correlate` correlaciona EXCLUSIVAMENTE pelo nome remoto exato (`PurviewRemotePstName`, derivado de `ArtifactId` — globalmente único, nunca reaproveitado entre ondas) contra o conjunto canônico RESOLVIDO na chamada atual (nunca um snapshot antigo); um nome remoto fora desse conjunto (desconhecido, de outra onda, de outro escopo) recusa o relatório inteiro fail-closed — nunca descartado silenciosamente. |
| Relatório que afirma cobrir 100% dos PSTs da onda, mas na verdade cobre um subconjunto (completude forjada) | A diretiva opcional `#TotalRows:<N>` é verificada em DUAS camadas: o parser recusa fail-closed se a contagem declarada divergir da contagem REAL de linhas no próprio arquivo; a correlação recusa fail-closed se a contagem declarada como completa não cobrir EXATAMENTE o conjunto canônico da onda. Quando o relatório nunca declara completude, um subconjunto é aceito como evidência PARCIAL (nunca um erro) e a avaliação de completude separada classifica corretamente como `Incomplete`. |
| Avaliação de completude deste Passo confundida com aprovação/certificate/conclusão de onda | `PurviewServiceResultCompletenessOutcome` expõe SOMENTE `CompleteForProviderEvidence`/`Incomplete`/`Inconclusive` — nenhum valor `Pass`/`Certificate`/`Completed` existe no enum (comprovado por `CompletenessNeverExposesAFinalPassOrCertificateOutcome`); nenhum caso de uso deste Passo referencia `ArchiveBridge.Domain.Reconciliation.ReconciliationOutcome` nem qualquer API de fechamento de onda/projeto. |
| Evidência persistida (plano, vínculo, observação, versão do relatório, linha normalizada) adulterada diretamente no SQL e lida como canônica | Mesma fronteira NÃO CONFIÁVEL de `WavePartitionOutputBinding`/`PurviewMappingCsvVersion`: `PurviewImportJobPlan.Rehydrate`, `PurviewImportJobObservation.Rehydrate` e `PurviewServiceResultReportEvidence.Rehydrate` recomputam o hash determinístico a partir de TODOS os campos REALMENTE carregados e recusam fail-closed qualquer divergência; `SqlPurviewServiceResultReportStore.GetRowsAsync` recomputa `PurviewServiceResultRowsHash` sobre as linhas efetivamente lidas e recusa fail-closed (inserção/remoção/duplicação/alteração de qualquer linha é detectada, não só alteração de campo). |
| `Complete`/`Analysis completed`/`Import completed` no portal tratado como encerramento automático da wave/projeto | `PurviewImportJobObservedStatus` documenta explicitamente que NENHUM valor (inclusive `ImportCompleted`) encerra a onda/projeto; nenhum caso de uso deste Passo marca onda/projeto como `COMPLETED`, emite certificate ou dispara qualquer efeito de conclusão — apenas persiste a observação como evidência. |
| Corrida concorrente duplicando plano/observação/versão do relatório, ou perdendo uma mudança real | Planejamento: sequência de tentativa alocada sob `UPDLOCK, HOLDLOCK` na MESMA transação do insert (mesmo padrão de `mapping_version`); idempotente pela impressão digital (mesma evidência ⇒ mesmo plano). Observação: `RecordObservationAsync` verifica/reivindica o vínculo plano→provider e detecta replay idêntico DENTRO da MESMA transação curta — nunca em duas leituras separadas sujeitas a corrida. Relatório: versão alocada sob lock; idempotência por `content_sha256` com índice único `(wave_id, attempt_sequence, content_sha256)` como backstop SQL. |
| Dependência vazando de Domain/Application para Purview SDK/browser automation/Graph/EXO/vendor concreto | Nenhum pacote/assembly de fornecedor é referenciado por `ArchiveBridge.Domain`/`ArchiveBridge.Application`/`ArchiveBridge.Contracts` deste módulo — o parser/correlação/completude são funções puras; a única integração com o Passo 4 é via `ResolvePurviewMappingEvidenceUseCase` (já auditado, Domain/Application-only). Verificado pelos testes já existentes de `VendorBoundaryTests`/`DependencyRuleTests` (sem necessidade de nova allowlist — nenhum token de vendor novo aparece no código deste Passo). |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos módulos anteriores. |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Automação do portal Purview, criação/validação/salvamento/início automático de import job, clique/execução
automática de `Import data`, scraping/browser automation do Purview, Graph/EXO writes, `Enable-Mailbox`,
alteração de hold/retention/auto-expansion, coleta real automatizada de estatísticas EXO post-import,
cálculo final expected-vs-observed entre origem e EXO, disposition/aprovação de exceções, emissão/assinatura
do certificado final, marcar wave/projeto `COMPLETED`, decommission/freeze/retention change no EV, início do
I7 Hardening. Nenhum destes fluxos existe no código deste Passo — não há superfície de ameaça nova a
analisar para eles aqui.
