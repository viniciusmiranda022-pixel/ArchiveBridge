# Protocolo de validação do Purview Network Upload — gate do ADR-0006

Evidência requerida pelo gate do
[ADR-0006](../0006-purview-adapter-ga-inicial.md) (Purview Network Upload
como adapter GA inicial). Este documento é o **protocolo de validação** — o
roteiro de testes e os critérios de aceitação que o **relatório de validação
em tenant controlado** deve preencher.

- **Tipo:** protocolo de validação em tenant Microsoft 365 controlado
- **Evidence Owner:** _a atribuir_ (Engenharia + responsável técnico pelo tenant)
- **Revisor necessário:** responsável técnico pelo tenant
- **Estado da execução:** **pendente** — nenhuma execução em tenant foi
  realizada até esta data. Este documento **não** é o relatório de
  validação nem a aceitação formal.

> [!IMPORTANT]
> A execução deste protocolo é **externa a esta sessão** e depende de um
> tenant Microsoft 365 controlado, de contas com os papéis mínimos e da
> autorização do Decision Owner. Enquanto o relatório de validação não
> estiver anexado e revisado, o [ADR-0006](../0006-purview-adapter-ga-inicial.md)
> permanece `proposto`.

## 1. Objetivo

Confirmar, em tenant controlado, que o adapter Purview Network Upload
prepara, transporta e reconcilia uma onda de PST **pelo único caminho
suportado pela Microsoft** (portal Purview + AzCopy + CSV mapping oficial),
respeitando o capacity gate e o bloqueio de >100 GB, e que o fluxo é
compatível com a **baseline on-premises** ([ADR-0003](../0003-azure-sql-e-service-bus-premium.md)):
AzCopy a partir de worker on-premises e SAS custodiado pelo mecanismo de
segredos on-premises (detalhe em [ADR-0008](../0008-isolamento-por-tenant-e-projeto.md)).

## 2. Pré-requisitos

- Tenant Microsoft 365 controlado (não produção do cliente), com licença
  que habilite archive quando o caso de teste exigir.
- Role group dedicado `PST Import Operators` com `Mailbox Import Export` e
  `Mail Recipients`; **Global Administrator rejeitado como conta operacional**
  (§25.1). Contas com MFA/Conditional Access/PIM.
- Aprovador distinto do operador que inicia a onda (§25.1).
- Upload worker on-premises dedicado e endurecido: Windows Service, sem
  usuários interativos, admin JIT, AzCopy homologado (binário + SHA-256),
  transcript desabilitado (§25.6, item 3 do ADR-0006).
- Mecanismo de segredos on-premises disponível para custódia do SAS (ADR-0003/ADR-0008).

## 3. Casos de validação e critérios de aceitação

| # | Caso | Fonte | Critério de aceitação |
| --- | --- | --- | --- |
| V1 | Permissões mínimas: criar job com role group dedicado; recusar Global Administrator como conta operacional | §25.1 | Job criável com o role restrito; conta GA não usada como operacional |
| V2 | Precheck de tenant/mailbox (archive status, GUIDs, holds, auto-expanding) por leitura restrita | §25.2 | Precheck coleta as propriedades estruturadas; nenhuma mudança implícita (archive/auto-expansion) executada |
| V3 | Capacity gate — dentro do limite | §25.4 | Onda dentro do limite passa; `csvRowCount ≤ 500`; `targetRoot != "/"` |
| V4 | Capacity gate — **bloqueio >100 GB no mesmo archive** | §25.4, §27 | Estado `MICROSOFT_ASSESSMENT_REQUIRED`; `AutoExpandingArchiveEnabled=True` **não** eleva o limite; job vai a `WAITING_EXTERNAL` com o pacote de suporte (§27) |
| V5 | Coleta segura do SAS pelo formulário secreto; custódia no mecanismo de segredos on-premises | §25.5, ADR-0006 item 4 | SAS validado (host/HTTPS/container `ingestiondata`/expiry/permissões); **nunca** em log/analytics/telemetria; leitura restrita à identidade do worker; eliminação após upload |
| V6 | Transporte AzCopy a partir do worker on-premises | §25.6 | Upload concluído; versão AzCopy homologada; SAS redigido em exceções/telemetria; `UPLOAD_VERIFIED` |
| V7 | Builder do CSV mapping oficial | §25.8 | Dez colunas e cabeçalho idênticos; `Workload=Exchange`; `FilePath` sem `ingestiondata` e case-sensitive; `IsArchive=TRUE` só após precheck; `TargetRootFolder=/ImportedPst_<Project>_<Wave>`; ≤ 500 linhas; SHA-256 do CSV registrado |
| V8 | Workflow humano no portal Purview (criar/validar/iniciar job) | §25.9 | Passos executados no portal; CSV não editado manualmente; nome/ID do job, operador, horário e relatório registrados |
| V9 | Ledger `external_operations` para upload + import job | ADR-0006 item 5, ADR-0003 | Transições `INTENT → SUBMITTED → CONFIRMED/AMBIGUOUS/FAILED` registradas com chave visível ao provedor (Purview job ID); retomada idempotente após interrupção não duplica submissão |
| V10 | Reconciliação pós-import | §26 | Resultado classificado (`PASS` / `PASS_WITH_EXPLAINED_EXCEPTIONS` / `INCONCLUSIVE` / `FAIL` / `DUPLICATE_RISK`) com evidência completa; Retention Hold nunca removido automaticamente (§26.4) |
| V11 | Retenção do staging Microsoft | §25.10 | Registrado que o produto não promete deleção imediata; limitação no data processing record |

## 4. Artefatos de evidência a coletar

- Saída do precheck (V2) e do capacity gate (V3/V4), incluindo o pacote de
  suporte do cenário >100 GB (§27).
- CSV mapping gerado + SHA-256 (V7); validation report do Purview (V8).
- AzCopy result/plan/log **sanitizados** (sem query string do SAS) (V6).
- Registro do ledger `external_operations` (V9).
- Estatísticas EXO antes/depois e resultado de reconciliação (V10, §26).
- Nome/ID do Purview job, operador, horário e screenshots/relatórios (V8).

## 5. Conclusão e assinatura (a preencher na execução)

- **Resultado geral:** _(pendente)_
- **Ressalvas/limitações:** _(pendente)_
- **Evidence Owner (assinatura/data):** _(pendente)_
- **Revisor — responsável técnico pelo tenant (parecer/data):** _(pendente)_

A **aceitação formal** do ADR-0006 é ato do Decision Owner (Vinicius
Miranda) e ocorre **somente após** este protocolo ser executado, o relatório
de validação ser anexado e a revisão do responsável técnico pelo tenant ser
registrada — conforme a [matriz de fechamento](../gate-closure-matrix.md).
