# Threat model — I6/EPIC-07, Passo 5 (Reconciliation Certificate)

Delta sobre o modelo de ameaças da plataforma, incorporado abaixo do capítulo do Passo anterior
([`threat-model-slice-06-i6-passo3.md`](threat-model-slice-06-i6-passo3.md), Passo 3 — mesmo formato; o
Passo 4/AB-I6-010, workflow de disposition humano/auditável, não introduziu nenhuma superfície de ameaça
nova além das já cobertas nesse mesmo capítulo e não teve arquivo próprio). Escopo: materialização
IMUTÁVEL, determinística, tamper-evident e verificável offline do resultado técnico de reconciliação de uma
wave (work order [`AB-I6-013.md`](../engineering/requests/AB-I6-013.md)) — construída EXCLUSIVAMENTE sobre a
avaliação canônica já revalidada do Passo 3 e as dispositions humanas vigentes do Passo 4. **Sem** marcar
wave/projeto `COMPLETED`, **sem** sign-off final/cliente/jurídico, **sem** publicação WORM
institucional/decommission, **sem** freeze/unfreeze ou retention change no Enterprise Vault, **sem** writes
em Exchange Online/Graph/Purview, **sem** `Enable-Mailbox`/auto-expansion/hold change, **sem** automação do
portal/import job e **sem** I7 Hardening/I8 Production Acceptance (STOP-THE-LINE do work order). Nenhum
destes fluxos existe no código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.

Este é o último item documentado do EPIC-07 (`docs/runbook/06-parte-vi-plano-desenvolvimento.md`): o job de
reconciliação passa a produzir, por wave, um certificate imutável cujo `PASS`/`PASS_WITH_EXPLAINED_EXCEPTIONS`
exige 100% de evidence completeness e cadeia íntegra vigente.

## Ativos adicionais

- **Reconciliation certificates** (`purview_reconciliation_certificates`): header IMUTÁVEL e versionado por
  `(onda, plano de import job)` — resultado canônico (`ReconciliationOutcome`), completude de evidência
  (contagens `total_item_count`/`incomplete_item_count`, nunca um percentual isolado sem as contagens que o
  sustentam), resumo estruturado de desvios (`deviation_count`/`deviations_sha256`), sinal de duplicidade
  (`duplicate_risk_detected`, `mapping_fingerprint`), impressão digital de convergência idempotente
  (`evaluation_fingerprint`) e hash de integridade do próprio header (`certificate_hash`). Nunca contém
  mailbox/path/conteúdo de mensagem — apenas IDs opacos, contagens e hashes canônicos (item 11 do work
  order).
- **Trilha de auditoria do certificate** (`purview_reconciliation_certificate_audit_events`): evento
  append-only por emissão/convergência/verificação/supersession/falha de integridade — ator, papel,
  correlação, sucesso/motivo, sem segredo/PII (item 20).

## Classificação de dados

Nenhuma das duas tabelas novas é "zero PII" no sentido absoluto — `issued_by`/`actor_id` são a mesma
identidade operacional já persistida em `decided_by` (Passo 4) e `owner`/`decided_by` de outros módulos —
mas nenhuma delas contém SAS, credencial, token, conteúdo de e-mail, mailbox/UPN, caminho local ou stack
trace. O certificate deliberadamente NÃO duplica os itens/dispositions de origem (já persistidos e
auditáveis nas tabelas do Passo 3/4): referencia-os por contagens agregadas e por um hash agregado
ORDEM-INDEPENDENTE (`deviations_sha256`) do conjunto (`item_kind`, `item_key`, disposition técnica, código
de desvio) — nunca pelo conteúdo pleno de cada item.

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| **Certificate forgery** — um `PASS`/`PASS_WITH_EXPLAINED_EXCEPTIONS` emitido sem 100% de evidence completeness ou com uma exceção material sem disposition vigente aceita | `ReconciliationCertificateRules.DetermineResult` é uma função PURA e determinística que aplica, na ordem, os gates de `DuplicateRisk` → onda sem item canônico → `BlockedIntegrity` (sempre `Fail`, indeclinável) → completude < 100% (sempre `Inconclusive`, MESMO quando uma disposition `AcceptedException` existe sobre o item `IncompleteEvidence` — aceitar o risco operacional nunca torna a evidência completa, item 4/36) → exceção `Mismatch`/`ExtraInProvider` sem `AcceptedException` vigente (sempre `Fail`) → só então `PassWithExplainedExceptions`/`Pass`. Nenhum caminho de código permite a `Application`/`Infrastructure` sobrescrever esse resultado; `IssueReconciliationCertificateUseCase` sempre recomputa o resultado a partir do estado REAL resolvido server-side antes de chamar a store. Comprovado exaustivamente por `ReconciliationCertificateDomainTests` (todas as combinações de precedência) e end-to-end por `ReconciliationCertificateIntegrationTests` (happy `PASS`, `PASS_WITH_EXPLAINED_EXCEPTIONS`, `Fail` por `RemediationRequired`/ausência de disposition, `Inconclusive` por evidência incompleta mesmo com `AcceptedException`). |
| **Stale evidence** — certificate emitido/lido sobre uma cadeia canônica (mapping/upload/service result/EXO/avaliação) que já não é mais a vigente | `IssueReconciliationCertificateUseCase` reexecuta `EvaluateReconciliationUseCase` (que por sua vez roda `PurviewImportJobEvidenceGuard.ResolveAndVerifyNoDriftAsync` SEMPRE) a cada emissão — nunca lê uma avaliação já persistida como se fosse necessariamente vigente (item 3). Na LEITURA, `GetReconciliationCertificateUseCase` recomputa `IsSuperseded` comparando a versão de avaliação e o `deviations_sha256` do certificate contra a avaliação/dispositions REALMENTE vigentes agora — um certificate stale permanece histórico (nunca apagado/reescrito) mas nunca é apresentado como vigente sem essa marca explícita (item 15/18, comprovado por `GetIdentifiesAPreviouslyCurrentCertificateAsSupersededAfterNewCanonicalEvidenceWithoutDeletingIt`). |
| **Replay** — reenvio da mesma emissão produzindo um certificate duplicado ou um efeito não idempotente | `ReconciliationCertificate.EvaluationFingerprint` (item 16) é computado exclusivamente sobre a evidência-fonte (`assessment_source_fingerprint`, `deviations_sha256`, `duplicate_risk_detected`) — NUNCA sobre versão/timestamp/ator. `SqlReconciliationCertificateStore.IssueOrConvergeAsync` locka (`UPDLOCK, HOLDLOCK`) as versões existentes do escopo e converge para a vigente quando o fingerprint coincide, sem inserir uma nova linha — comprovado por `IssueConvergesIdempotentlyForAnIdenticalReplay`. |
| **Concurrent issuance** — N emissões concorrentes (idênticas ou com evidência mudando no meio) produzindo certificates duplicados ou um snapshot misto de evidência antiga/nova | Mesma técnica de lock dos Passos 3/4 sobre a MESMA faixa de linhas do escopo, serializando `IssueOrConvergeAsync` com `SqlReconciliationAssessmentStore.PersistAsync`/`SqlReconciliationExceptionDispositionStore.SaveDecisionAsync`. Sob o lock, a store revalida (1) que a versão de avaliação segue vigente e (2) que `ReconciliationExceptionDecisionsStateHash` das decisões REALMENTE lockadas corresponde ao esperado pela `Application` — qualquer divergência recusa `ReconciliationCertificateStaleChainException` fail-closed em vez de persistir um certificate sobre snapshot misto (item 17/49, comprovado por `IssueOrConvergeFailsClosedWhenTheAssessmentVersionIsNoLongerCurrent`/`IssueOrConvergeFailsClosedWhenTheDecisionsStateFingerprintDivergesFromWhatWasActuallyLocked`). Emissões concorrentes IDÊNTICAS convergem para uma única versão (item 11, comprovado por `IssueConvergesUnderFiveConcurrentIdenticalIssuancesInsteadOfDuplicating`). |
| **Tampering** — certificate adulterado diretamente no SQL (resultado, ator, hash de desvios, etc.) lido como canônico | Fronteira NÃO CONFIÁVEL, mesmo princípio dos Passos 3/4: `ReconciliationCertificate.Rehydrate` recomputa `certificate_hash` a partir de TODOS os campos REALMENTE carregados e recusa fail-closed (`ReconciliationCertificateIntegrityViolationException`) qualquer divergência — comprovado por adulteração direta de `result`/`issued_by`/`deviations_sha256` (`GetLatestFailsClosedWhenTheResultIsTamperedDirectlyInSql` e afins). O certificate não possui artefato físico separado (nenhuma serialização em arquivo) — a própria linha SQL, tamper-evident por hash, É o documento canônico verificável offline (item 12/14: a verificação nunca depende de nova chamada a Purview/EXO/EV, apenas de recomputar o hash a partir dos campos já persistidos). |
| **Anti-IDOR** — certificate de outro tenant/projeto/onda acessado, emitido ou verificado por IDOR | Toda operação (`IssueOrConvergeAsync`, `GetLatestAsync`, `GetByVersionAsync`, `GetHistoryAsync`, `GetLatestForWaveAcrossOtherAttemptsAsync`, `RecordAuditEventAsync`) recebe `TenantScope` explícito e as duas tabelas novas participam de `rls.tenant_isolation_policy` (FILTER + BLOCK); a resolução do `attempt_sequence` filtra `project_id` explicitamente antes de qualquer lock/leitura — um certificate de outro escopo é indistinguível de inexistente (`PurviewImportJobSourceNotFoundException`), comprovado por `IssueFailsClosedWhenTheWaveDoesNotBelongToTheCallersScope` (item 18). |
| **PII minimization** — o certificate se tornando um novo ponto de agregação de PII desnecessária | O certificate NUNCA persiste mailbox/UPN/path/conteúdo de mensagem — apenas contagens agregadas e `deviations_sha256` (hash, não os itens). Quando uma leitura precisa dos desvios individuais, ela é recomputada sob demanda a partir das tabelas de origem já auditadas do Passo 3/4 (`ReconciliationCertificateRules.BuildDeviationSummary`), nunca duplicada em uma terceira tabela (item 11). |
| **Interpretação dos outcomes** — `PASS`/`PASS_WITH_EXPLAINED_EXCEPTIONS` sendo mal interpretados como conclusão de projeto, sign-off ou decommission | O certificate nunca marca `WaveStatus`/`ProjectStatus`, nunca escreve em EXO/Graph/Purview/EV e nunca invoca nenhum adapter de write — comprovado end-to-end por `IssueNeverChangesTheWaveStatus` (item 19, mesmo padrão de `DisposeNeverChangesTheWaveStatus` do Passo 4). O tipo `ReconciliationOutcome` reaproveitado é exatamente a taxonomia já documentada em runbook §26.3 (`PASS`/`PASS_WITH_EXPLAINED_EXCEPTIONS`/`INCONCLUSIVE`/`FAIL`/`DUPLICATE_RISK`) — nenhum estado alternativo foi inventado. |
| **RBAC/spoofing** — ator sem papel adequado emitindo um certificate, ou alegando um papel via payload | `IssueReconciliationCertificateCommand` não carrega ator/papel algum (mesmo princípio AB-I6-012 do Passo 4) — identidade/papéis são SEMPRE resolvidos server-side via `IAuthenticatedActorAccessor`, antes de qualquer leitura de dado de escopo. Emissão exige `Administrator`/`Approver` (mesmo par de papéis de aprovação do Passo 4); leitura/verificação são puras, sem RBAC adicional além do já aplicado nas telas do portal (comprovado por `IssueRejectsAnUnauthorizedRole`/`IssueFailsClosedBeforeAnyScopedReadWhenThereIsNoAuthenticatedPrincipal`/`IssuePersistsTheServerSidePrincipalAsIssuedByNeverAValueFromTheCommand`). |
| Dependência vazando de Domain/Application para Purview SDK/browser automation/Graph/EXO/vendor concreto | Nenhum pacote/assembly de fornecedor é referenciado por `ArchiveBridge.Domain`/`ArchiveBridge.Application`/`ArchiveBridge.Contracts` deste módulo — `ReconciliationCertificate`/`ReconciliationCertificateRules`/os hashes agregados são registros/funções puros; a única integração externa é com os Passos 1-4 (já auditados, Domain/Application-only). Verificado pelos testes já existentes de `DependencyRuleTests` (sem necessidade de nova allowlist). |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos módulos anteriores. |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Marcar wave/projeto `COMPLETED`, sign-off final/cliente/jurídico automático, publicação WORM institucional
ou decommission, freeze/unfreeze/retention change no Enterprise Vault, writes em Exchange
Online/Graph/Purview, `Enable-Mailbox`/auto-expansion/hold change, automação do portal Purview/import job,
I7 Hardening (DR/chaos/performance/WDAC/pen-test) ou I8 Production Acceptance (canário/go-live). Nenhum
destes fluxos existe no código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.
