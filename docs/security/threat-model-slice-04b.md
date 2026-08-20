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
| Path traversal / escape da raiz de custódia | `PstRelativePath` (Domain) rejeita, na FORMA do texto, caminho absoluto (incluindo rótulo de unidade Windows — verificação deliberadamente independente de `Path.IsPathRooted`, cujo resultado varia por plataforma) e segmentos de travessia (`.`/`..`). Em Infrastructure, `ArtifactPathContainment.EnsureContained` (já usado por `FileSystemMappingArtifactStore`) canonicaliza contra a raiz configurada e rejeita qualquer symlink/reparse point na cadeia — inclusive no componente final (o próprio arquivo) — antes de abrir. Defesa em profundidade: forma no Domain, I/O real em Infrastructure. |
| TOCTOU (arquivo trocado entre custódia e leitura) | O hash é sempre recalculado NA LEITURA (streaming, cobrindo o arquivo inteiro) e comparado ao hash registrado em custódia; divergência ⇒ `Stale`, fail-closed, nunca reaproveita um resultado anterior. Arquivo removido/inacessível entre a resolução do caminho e a abertura vira `ReadError` sanitizado, nunca um crash não tratado. |
| Arquivo malformado/hostil derruba o worker ou produz sucesso falso | A engine nunca lança exceção não tratada para um arquivo ilegível/inválido/truncado — todo erro de leitura vira `PstStructuralDiagnostic.ReadError`; um cabeçalho que não bate com a assinatura PST vira `InvalidSignature`/`InvalidClientSignature`/`UnsupportedVersion`/`TooSmall`, nunca `Valid`. Nenhum destes diagnósticos executa parser de terceiro (sem superfície de exploit de biblioteca de PST). |
| Exaustão de recursos (arquivo enorme, leitura infinita) | `PstStorageOptions.MaxSizeBytes` rejeita fail-closed (`PstInspectionLimitExceededException`, outcome `LimitExceeded`) antes de abrir o stream; `PstStorageOptions.Timeout` aborta via `CancellationTokenSource.CancelAfter` distinto do cancelamento do chamador. Nenhum destes casos é reportado como sucesso. |
| Replay/duplicação de efeito | Réplay idempotente: mesmo artefato + mesmo hash de custódia ⇒ resultado canônico reaproveitado, a engine NÃO é reinvocada (`FindCanonicalAsync` antes de `InspectAsync`). |
| Corrida de gravação (dois workers inspecionam o mesmo artefato ao mesmo tempo) | Índice único **filtrado** `UX_pst_inspections_canonical (tenant_id, project_id, artifact_id, expected_hash) WHERE outcome = 0` é o backstop SQL; a Application captura `PstInspectionConflictException` e relê o canônico já persistido (nunca duas linhas canônicas para o mesmo artefato/hash). Comprovado por `ConcurrentInspectionOfTheSameArtifactConvergesToExactlyOneCanonicalRecord` (6 chamadas concorrentes ⇒ 1 linha canônica). |
| Custódia registrada duas vezes para o mesmo caminho | `UQ_pst_artifacts_path (tenant_id, project_id, relative_path)` impede duplicidade silenciosa de registro dentro do mesmo escopo. |
| Vazamento de caminho real/stack trace em log/evidência | Todo erro de I/O é capturado e sanitizado em `PstStructuralDiagnostic.ReadError`/`PstInspectionLimitExceededException` (`ReasonCode` curto, sem interpolar caminho ou mensagem de exceção bruta). |
| SQL injection | Todo acesso a dados é parametrizado (`SqlCommand`/`SqlParameter`), sem concatenação de entrada. |

## Fora de escopo desta fatia (herdado do STOP-THE-LINE)

Export-EVArchive, split/partition execution, repair de PST, Outlook automation, upload/AzCopy/Azure
staging, Purview/Graph/Exchange Online/import job, reconciliação M365. Nenhum destes fluxos existe no
código deste Passo — não há superfície de ameaça nova a analisar para eles aqui.
