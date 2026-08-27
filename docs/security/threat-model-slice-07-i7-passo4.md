# Threat model — I7/EPIC Hardening, Passo 4 (AB-I7-008)

Delta sobre o modelo de ameaças da plataforma, incorporado abaixo do capítulo do Passo anterior de
Hardening ([`../engineering/dr-readiness-matrix.md`](../engineering/dr-readiness-matrix.md), AB-I7-005 —
mesmo formato de documento, sem arquivo `threat-model-*` próprio porque aquele Passo é predominantemente
evidência de DR/recovery, não superfície nova). Escopo: **App Control/WDAC evidence, worker hardening
baseline, supply-chain build provenance, incident-response exercitado e o boundary explícito de pen-test**
(work order [`AB-I7-008.md`](../engineering/requests/AB-I7-008.md)). **Sem** declarar `Production
Ready`/`GoLive`/canário aprovado/I8 concluído, **sem** alegar pen-test institucional/independente, **sem**
aplicar WDAC/GPO/Defender/Intune/Azure Policy a nenhum host real, **sem** abrir inbound/RDP/SMB ou relaxar
firewall/ACL, **sem** introduzir dependência obrigatória de Azure PaaS, **sem** novo endpoint/write-path de
produção (STOP-THE-LINE do work order). Nenhum destes fluxos existe no código deste Passo — não há
superfície de ameaça nova a analisar para eles aqui.

## Ativos adicionais

- **Worker hardening evidence** (`security_worker_hardening_evidence`): um registro IMUTÁVEL e versionado
  por (tenant, projeto, controle) — desfecho (`WorkerHardeningStatus`), aplicabilidade FIXA derivada do
  catálogo (`WorkerHardeningApplicability`, nunca informada pelo chamador), medição real opcional
  (`WorkerHardeningMeasurement`), digest de evidência, motivo documentado quando bloqueado. Nunca contém
  configuração/segredo/credencial — apenas metadados de verificação.
- **WDAC/App Control policy evidence** (`security_wdac_policy_evidence`): header versionado por (tenant,
  projeto) com as entradas da allowlist codificadas canonicamente (publisher/hash/path-rule) e um digest
  determinístico sobre TODAS as entradas. Nenhuma entrada allow-all é aceita pelo domínio antes de chegar
  ao banco.
- **Supply-chain build provenance** (`security_build_provenance`): identidade determinística de UMA build
  aprovada por (tenant, projeto, nome do artifact) — commit SHA-1 (40 hex), identidade do builder, instante
  da build, digest do artifact. Usada por `ArtifactPromotionVerifier` para recusar promoção com drift.
- **Incident-response drills** (`security_incident_response_drills`): evidência de um exercício sintético e
  não destrutivo por (tenant, projeto, tipo de drill) — desfecho real observado, timestamps, digest da
  evidência (NUNCA o segredo/PII bruto) e uma disposition operacional validada contra aparência de
  segredo/PII antes de aceitar.
- **Pen-test readiness bundle** (`security_pentest_readiness_bundles`): preparação interna versionada por
  (tenant, projeto) — resumos de escopo/superfície de ataque/trust boundaries/fixtures sintéticas/itens
  bloqueados e o digest da build alvo. `PenTestReadinessStatus` possui ESTRUTURALMENTE apenas dois valores
  (`NotPerformed`/`Blocked`) — nenhum caso que represente conclusão existe no tipo.
- **`SecretRedactor`** (`ArchiveBridge.Domain.Security`): utilitário centralizado de redação/detecção de
  segredo-PII (runbook §32.1), reutilizável por qualquer camada — não é uma tabela, mas um ativo de código
  crítico (a lacuna que este Passo fecha: não existia nenhum redator centralizado antes, apenas heurísticas
  pontuais como `ReconciliationExceptionCommentText.SuspectedSecretPattern`).

## Classificação de dados

Nenhuma das cinco tabelas novas carrega segredo, token, SAS, cabeçalho `Authorization`, cookie, `subject`/
`body`/nome de anexo, ou path sensível — apenas IDs opacos, enums, timestamps, texto técnico curto validado
fail-closed contra aparência de segredo/PII (`EvidenceText`/`SecretRedactor.ContainsSuspectedSecret`), e
hashes/digests SHA-256. `executed_by`/`approved_by`/`issued_by`/`prepared_by` são a mesma classe de
identidade operacional já persistida em outras tabelas do schema (ex.: `executed_by` em
`recovery_readiness_evidence`).

## Ameaças e mitigações (delta)

| Ameaça | Mitigação |
| --- | --- |
| **Fake compliance** — declarar um controle de hardening/WDAC/build/pen-test como aprovado sem evidência real (ex.: alegando um papel elevado) | Cada tipo de evidência exige uma medição/digest REAL para qualquer estado positivo: `WorkerHardeningControlRecord.Pass` exige `WorkerHardeningMeasurement` não-anulável; `WdacPolicyEvidence` só valida contra entradas REALMENTE persistidas e revalidadas; `BuildProvenanceRecord`/`ArtifactPromotionVerifier` comparam digests, nunca aceitam alegação; `PenTestReadinessStatus` não possui NENHUM caso que represente conclusão — nenhum papel/ator pode produzir esse estado porque ele não existe no tipo. Comprovado por `WorkerHardeningControlRecordTests`/`PenTestReadinessBundleTests`/`SecurityHardeningEvidenceArchitectureTests`. |
| **Privilege spoofing de aplicabilidade** — um ator alegando um papel elevado reclassificando um controle `Unsupported` como `Required`/`Pass`, ou forjando um resultado proibido via INSERT direto | `WorkerHardeningApplicability` é SEMPRE derivada de `WorkerHardeningBaselineCatalog` (nunca um parâmetro do chamador) — impossível de sobrescrever em nenhum caminho de código. Defesa em profundidade no schema: `CK_whe_mde_never_pass` e `CK_prb_status_never_pass` tornam a linha proibida IRREPRESENTÁVEL mesmo por um INSERT direto adulterado que reivindique `executed_by_role = 'Administrator'`. Comprovado por `WorkerHardeningControlRecordTests.AnUnsupportedControlCanNeverResultInPassEvenWithARealMeasurement` e `SecurityHardeningEvidenceIntegrationTests.TheDatabaseItselfRejectsAWorkerHardeningRowClaimingAnUnsupportedControlPassed`/`TheDatabaseItselfRejectsAPenTestReadinessRowClaimingACompletedPassStatus`. |
| **WDAC allow-all** — uma entrada da allowlist configurada tão ampla que efetivamente permite qualquer binário | `WdacAllowlistEntry.Create` recusa (a) ausência de hash E de publisher+path-rule, e (b) qualquer path rule composta somente por curingas/separadores — em ambos os casos antes mesmo de persistir. Comprovado por `WdacPolicyEvidenceTests.AnEntryWithNoHashAndNoScopedPathIsRejectedAsAllowAll`/`AnEntryWithAWildcardOnlyPathRuleAndNoHashIsRejectedAsAllowAll`. |
| **Tampering** — qualquer uma das cinco evidências adulterada diretamente no SQL e lida como canônica | Fronteira NÃO CONFIÁVEL, mesmo princípio dos Passos anteriores: cada `Rehydrate` recomputa o(s) hash(es) a partir dos campos REALMENTE carregados (incluindo `PolicyDigest` sobre as entradas decodificadas de `entries_canonical`) e recusa fail-closed qualquer divergência — `WorkerHardeningIntegrityViolationException`/`WdacPolicyIntegrityViolationException`/`SupplyChainIntegrityViolationException`/`IncidentResponseIntegrityViolationException`/`PenTestReadinessIntegrityViolationException`. Comprovado por adulteração direta em cada suíte de domínio e por `SecurityHardeningEvidenceIntegrationTests.WorkerHardeningRecordControlAsyncConvergesIdempotentlyAndTamperedRowsFailClosedOnRead`/`WdacPolicyTamperedEntriesFailClosedOnRead`. |
| **Supply-chain drift** — um artifact promovido que NÃO é bit-a-bit o que foi aprovado (build substituída/comprometida entre aprovação e promoção) | `ArtifactPromotionVerifier.VerifyPromotion` compara `ArtifactDigest` do candidato contra o da build aprovada e SEMPRE lança `SupplyChainPromotionDriftException` em qualquer divergência — nunca um retorno booleano que um chamador possa ignorar silenciosamente. Comprovado por `BuildProvenanceRecordTests.VerifyPromotionFailsClosedWhenTheCandidateDigestDriftsFromTheApprovedBuild`. |
| **Secret/PII leakage em evidência/log** — SAS, `Authorization`, cookie, bearer token, UPN/e-mail, caminho UNC ou `subject`/`body`/nome de anexo persistidos em texto livre | `SecretRedactor.Redact` centraliza a redação (runbook §32.1); `EvidenceText`/`SecretRedactor.ContainsSuspectedSecret` é usada como GUARDA DE ACEITAÇÃO fail-closed em TODO campo de texto livre novo (`Notes`/`BlockedReason` do worker hardening, `Disposition` do incident-response, os resumos do pen-test bundle) — um valor com aparência de segredo/PII é recusado na CONSTRUÇÃO do registro, nunca persistido para depois ser redigido. Comprovado por `SecretRedactorCanaryTests` (11 formas canárias injetadas) e pelos testes `*IsRejectedFailClosed` de cada tipo de evidência. |
| **Anti-IDOR / cross-tenant** — evidência de outro tenant/projeto lida, emitida ou adulterada por IDOR | Todas as 5 tabelas participam de `rls.tenant_isolation_policy` (FILTER + BLOCK), mesmo mecanismo já aceito desde 0003; toda operação de store recebe `TenantScope` explícito — um registro de outro escopo é indistinguível de inexistente (`null`), nunca uma exceção que revele existência. Comprovado por `SecurityHardeningEvidenceIntegrationTests.IncidentResponseDrillRecordDrillAsyncConvergesIdempotentlyAndIsInvisibleAcrossTenants` e pelo drill dedicado `IncidentResponseDrillHarnessIntegrationTests.TheCrossTenantDenialDrillProducesContainedEvidenceWhenRlsDeniesTheOtherTenantsRow`. |
| **Concurrent issuance** — N submissões concorrentes (idênticas ou com resultado mudando no meio) produzindo evidência duplicada | Mesma técnica de lock dos Passos anteriores (`WITH UPDLOCK, HOLDLOCK` sobre a faixa de linhas do escopo dentro da MESMA transação) nas cinco stores novas: convergência idempotente quando o `ContentFingerprint`/`PolicyDigest` é idêntico, nova versão apenas quando o resultado REALMENTE difere. |
| **RBAC/spoofing de identidade** — ator sem papel adequado registrando evidência, ou alegando um papel via payload | `executed_by`/`approved_by`/`issued_by`/`prepared_by`/`*_role` são metadados DESCRITIVOS persistidos como recebidos do composition root (mesmo padrão de `RecoveryReadinessRecord.ExecutedBy`) — NENHUM deles autoriza nada por si só; a autorização/RBAC de quem pode CHAMAR estas stores é responsabilidade do composition root de um incremento futuro que exponha um caso de uso real (este Passo não adiciona nenhum). O que este Passo garante é que NENHUM valor alegado nesses campos consegue alterar `Applicability`/desbloquear `Pass` estruturalmente proibido — ver a linha de "Privilege spoofing" acima. |
| Dependência vazando de Domain/Contracts para SDK de fornecedor/Azure PaaS obrigatório | `ArchiveBridge.Domain`/`ArchiveBridge.Contracts` deste módulo não referenciam nenhum pacote NuGet (verificado por `DependencyRuleTests.DomainDeclaresNoPackageReference`/`ContractsDeclaresNoPackageReference`, já existentes, sem necessidade de nova allowlist); `MdeTenantPolicyEnforcement` é modelado como `Unsupported` explicitamente para NUNCA introduzir uma dependência obrigatória de Azure Intune/Entra na baseline aceita. |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada — reaproveita o mesmo padrão dos módulos anteriores; a única concatenação de string em SQL é de identificadores de coluna/tabela fixos em tempo de compilação (`Columns`), nunca de valor de usuário. |

## Fora de escopo deste Passo (herdado do STOP-THE-LINE)

Declarar `Production Ready`/`GoLive`/canário aprovado/I8 concluído; alegar pen-test institucional/
independente sem relatório real; aplicar WDAC/GPO/Defender/Intune/Azure Policy a hosts reais de produção;
abrir inbound/RDP/SMB ou relaxar firewall/ACL para testes; armazenar segredo/token/SAS/`Authorization`/
`subject`/`body`/nome de anexo/path sensível em qualquer evidência (incluindo os próprios drills e
fixtures de teste deste Passo — apenas canários sintéticos); introduzir dependência obrigatória de Azure
PaaS; novo endpoint/write-path de produção (nenhuma página Razor/API nova em `ArchiveBridge.ControlPlane`).
Nenhum destes fluxos existe no código deste Passo — não há superfície de ameaça nova a analisar para eles
aqui. Pendente para I8/incremento futuro: pen-test independente real; policy WDAC compilada e aplicada a
uma imagem de worker real; integração automática do pipeline de CI com `IBuildProvenanceStore` para
aprovar builds sem intervenção manual.
