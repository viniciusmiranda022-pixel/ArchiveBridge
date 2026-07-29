# Protocolo de validação do Purview Network Upload — gate do ADR-0006

Evidência requerida pelo gate do
[ADR-0006](../0006-purview-adapter-ga-inicial.md) (Purview Network Upload
como adapter GA inicial). O protocolo é dividido em **dois gates claramente
separados** para **não exigir código inexistente** como evidência prévia de
aceitação do ADR.

- **Tipo:** protocolo de validação em tenant Microsoft 365 controlado (Gate A)
  + contrato de implementação (Gate B)
- **Evidence Owner:** _a atribuir_ — **Engenharia** produz e assina o relatório
- **Revisor necessário:** _a atribuir_ — **responsável técnico pelo tenant**
  (papel **distinto** do Evidence Owner)
- **Estado da execução:** **pendente** — nenhuma execução em tenant foi
  realizada. Este documento **não** é o relatório nem a aceitação formal.

> [!IMPORTANT]
> **Reclassificação (Decision Owner, 2026-07-28 — aceitação arquitetural do ADR-0006).**
> A decisão arquitetural foi **aceita**; nem o Gate A nem o Gate B bloqueiam
> essa aceitação. **Gate A** (validação operacional em tenant, executável
> **manualmente**, sem código) permanece **obrigatório antes de produção e da
> certificação** do adapter Purview — **está pendente**. **Gate B** (contrato
> de implementação, dependente de código) permanece **obrigatório antes de
> produção**. Exigir código inexistente como evidência prévia bloquearia
> indevidamente a decisão arquitetural.

## 1. Objetivo

Confirmar que o Purview Network Upload é o **caminho GA suportado pela
Microsoft** (portal + AzCopy + CSV mapping oficial), compatível com a
**baseline on-premises** ([ADR-0003](../0003-azure-sql-e-service-bus-premium.md)),
separando **o que se valida antes do código** (Gate A) do **que o código deve
garantir antes de produção** (Gate B).

---

## Gate A — Validação operacional em tenant (obrigatória antes de produção/certificação; **não** bloqueia a aceitação arquitetural)

Executável manualmente em **tenant controlado**, sem código do produto. Cada
item produz artefato anexável ao relatório. **Estado: pendente** — nenhuma
execução em tenant foi realizada; nenhum relatório é presumido.

| # | Caso | Fonte | Critério de aceitação |
| --- | --- | --- | --- |
| A1 | **Tenant controlado** provisionado (não produção do cliente) | §25 | tenant isolado, licença que habilite archive quando o caso exigir |
| A2 | **Permissões mínimas**: role group dedicado `PST Import Operators` (`Mailbox Import Export` + `Mail Recipients`); GA recusado como conta operacional; aprovador ≠ operador | §25.1 | job criável com role restrito; MFA/CA/PIM; segregação aprovador/operador |
| A3 | **Obtenção e validação do SAS** pelo formulário secreto | §25.5 | SAS validado (host/HTTPS/container `ingestiondata`/expiry/permissões); nunca ecoado |
| A4 | **Execução manual do AzCopy** para o staging Microsoft | §25.6 | upload concluído com AzCopy homologado; SAS não impresso |
| A5 | **CSV mapping oficial** (formato) montado à mão para a onda de teste | §25.8 | dez colunas e cabeçalho idênticos; `Workload=Exchange`; `FilePath` sem `ingestiondata`; `TargetRootFolder=/ImportedPst_<Project>_<Wave>`; ≤ 500 linhas; SHA-256 registrado |
| A6 | **Criação e início do job no portal Purview** | §25.9 | job criado/validado/iniciado no portal; nome/ID, operador e horário registrados |
| A7 | **Importação de PST sintético** (pequeno, sem PII) | §25.9 | PST sintético importado; status por arquivo coletado |
| A8 | **Relatórios do Purview** (validation report, status por PST, import size/count) | §26.1 | relatórios exportados e anexados |
| A9 | **Dados disponíveis para reconciliação** (EXO statistics antes/depois; granularidade do serviço) | §26.2 | métricas coletadas; granularidade documentada (o serviço pode não expor tudo) |
| A10 | **Limitações do serviço Microsoft** documentadas | §25.10, §27 | retenção do staging (~30 dias, sem deleção pelo operador); **comportamento do bloqueio >100 GB documentado a partir da documentação oficial — SEM executar carga real >100 GB nesta fase** |

**Regra do Gate A:** valida o **caminho suportado** e as **limitações do
serviço** — não valida código do produto. Uma **carga real acima de 100 GB
não é executada** aqui; o cenário >100 GB é confirmado por **documentação
oficial** e pelo pacote de suporte (§27).

---

## Gate B — Contrato de implementação (NÃO bloqueia a aceitação; obrigatório antes de produção)

Itens **dependentes de código** do produto. Tornam-se verificáveis quando o
scaffolding existir; **não** são exigidos para aceitar o ADR.

| # | Item | Fonte | O que o código deve garantir |
| --- | --- | --- | --- |
| B1 | **Capacity gate** | §25.4, §27 | bloqueia >100 GB no mesmo archive (`MICROSOFT_ASSESSMENT_REQUIRED`); auto-expanding não eleva o limite; `csvRowCount ≤ 500`; `targetRoot != "/"` |
| B2 | **CSV builder** | §25.8 | gera o CSV oficial com todas as validações; nunca edição manual; nova versão + hash a cada mudança |
| B3 | **Ledger `external_operations`** | ADR-0006 item 5, ADR-0003 | transições `INTENT → SUBMITTED → CONFIRMED/AMBIGUOUS/FAILED` persistidas |
| B4 | **`operation_key` determinística** | ADR-0006 item 5 | gravada em `INTENT` **antes** do efeito externo; **nome planejado do job** usado no portal; `provider_operation_id` registrado **após** a criação; reconciliação por **nome planejado + provider id** |
| B5 | **Crash recovery** | ADR-0003 | crash após `INTENT` e antes do portal não duplica submissão; retomada usa a `operation_key` existente |
| B6 | **Idempotência** | ADR-0003 | retomada não recria job nem reimporta parts já validadas |
| B7 | **Duplicate prevention** | ADR-0006 | tentativa de criar o mesmo job (nome planejado) não inicia segunda importação |
| B8 | **Leak test automático do SAS** | ADR-0006 "Exceção controlada" | varre logs/stdout/stderr/eventos/telemetria/evidência e **falha** se o SAS aparecer |
| B9 | **Reconciliação automática** | §26 | classifica `PASS`/`PASS_WITH_EXPLAINED_EXCEPTIONS`/`INCONCLUSIVE`/`FAIL`/`DUPLICATE_RISK`; Retention Hold nunca removido automaticamente (§26.4) |
| B10 | **Chaos tests** | ADR-0003 | timeout de consulta ao Purview, worker morto com lease ativo, resultado ambíguo (`AMBIGUOUS` nunca repete automaticamente) |

**Regra do Gate B:** é o **contrato de implementação verificável** antes de
produção. Sua ausência **não** bloqueia a aceitação do ADR — bloqueia a
**promoção a produção**.

---

## 2. Fronteira entre os gates (para não confundir)

- **Aceitação arquitetural do ADR-0006:** **já concedida** pelo Decision Owner
  em 2026-07-28 — **não** dependeu de Gate A nem de Gate B.
- **Certificar o adapter Purview e ir a produção** exige o **Gate A**
  (validação operacional em tenant + limitações do serviço, com revisão do
  responsável técnico pelo tenant) **e** o **Gate B** (contrato de
  implementação satisfeito e certificado, quando o código existir).
- **Nunca** exigir código inexistente (Gate B) como evidência prévia da
  decisão arquitetural.

## 3. Artefatos de evidência (Gate A) a coletar

- A2: definição do role group e prova de segregação aprovador/operador.
- A3–A4: registro do upload (AzCopy result/plan/log **sanitizados**, sem SAS).
- A5: CSV de teste + SHA-256.
- A6–A8: nome/ID do job, screenshots/relatórios do portal, validation report.
- A9: EXO statistics antes/depois; nota de granularidade do serviço.
- A10: documentação oficial das limitações (staging, >100 GB).

## 4. Conclusão e assinatura (Gate A — a preencher na execução)

- **Resultado do Gate A:** _(pendente)_
- **Limitações observadas do serviço:** _(pendente)_
- **Evidence Owner (Engenharia) — assinatura/data:** _(pendente)_
- **Revisor — responsável técnico pelo tenant (parecer/data):** _(pendente)_

A **aceitação arquitetural** do ADR-0006 foi registrada pelo Decision Owner
(Vinicius Miranda) em **2026-07-28** (ver "Registro de aceitação" no ADR e a
[matriz de fechamento](../gate-closure-matrix.md)). A **validação operacional
em tenant (Gate A) permanece pendente** e é **obrigatória antes de produção e
da certificação** do adapter; o **Gate B** segue como contrato de
implementação obrigatório antes de produção. **A aceitação do ADR não autoriza
produção.**
