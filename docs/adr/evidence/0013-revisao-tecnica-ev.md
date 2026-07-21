# Revisão técnica do capability discovery e dos adapters EV — gate do ADR-0013

Evidência requerida pelo gate do
[ADR-0013](../0013-exportacao-ev-multiversao.md) (exportação Enterprise
Vault multiversão por capability discovery).

- **Tipo:** revisão técnica de engenharia (Dev + Segurança)
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge, sob direção
  do Decision Owner
- **Data:** 2026-07-21
- **Natureza:** revisão **interna** das especificações de `docs/ev/`.
  Identifica pontos fortes, lacunas e riscos abertos; **não** é a aceitação
  formal (ato do Decision Owner). Como as especificações foram redigidas
  pela mesma engenharia, esta revisão é assumidamente **não independente** —
  a competência de revisão Dev + Segurança da [matriz](../gate-closure-matrix.md)
  é exercida/aceita pelo Decision Owner até haver revisor distinto.

## 1. Objeto revisado

Especificações em [`docs/ev/`](../../ev/README.md):
[capability-discovery](../../ev/capability-discovery.md),
[adapter-contract](../../ev/adapter-contract.md) (`IEvExportAdapter`),
[compatibility-matrix](../../ev/compatibility-matrix.md),
[legacy-adapter-framework](../../ev/legacy-adapter-framework.md),
[test-plan](../../ev/test-plan.md),
[operational-runbook](../../ev/operational-runbook.md).

## 2. Conformidade com a decisão do ADR-0013

| Requisito do ADR-0013 | Onde é atendido | Veredito |
| --- | --- | --- |
| Capability discovery **obrigatório** antes da seleção | capability-discovery §Regras (1–5); seleção consome só o relatório | **OK** |
| Nenhum adapter escolhido pelo número de versão | discovery detecta capacidades; `UNKNOWN` = fail closed | **OK** |
| Contrato comum `IEvExportAdapter` idêntico a todos | adapter-contract (9 operações + taxonomia única) | **OK** |
| Sem PowerShell arbitrário do Control Plane | legacy-framework: scripts assinados, allowlist, JSON | **OK** |
| Suporte só após laboratório/certificação por família | compatibility-matrix (compatível/testado/certificado) | **OK** |
| Ambiente sem adapter certificado = assistido/bloqueio | discovery §Seleção; matriz linha "assistido" | **OK** |
| Segmentação = política do produto validada por discovery | política de tamanho de PST (Detected*/OperationalTarget) | **OK** |

A especificação é **coerente com a decisão**. Nada de crítico contradiz o
ADR.

## 3. Pontos fortes

- **Fail closed** consistente: `UNKNOWN`, alvo fora do intervalo detectado,
  família sem adapter certificado — todos bloqueiam de forma controlada.
- **Idempotência e retry** com semântica explícita (`ExportRequestId`;
  retry não duplica o conjunto aprovado) — alinhado ao runbook §14/§20.
- **Segurança do legado** bem desenhada: assinatura + allowlist + contrato
  JSON + sem interpolação de shell reduzem a superfície.
- **Taxonomia única de exceções** permite o orquestrador tratar todos os
  adapters de forma uniforme.
- **Sanitização** exigida em toda saída (sem assunto/corpo/credencial/SAS).

## 4. Lacunas e riscos abertos (a resolver na implementação/certificação)

| # | Item | Severidade | Encaminhamento |
| --- | --- | --- | --- |
| L1 | Assinatura de scripts legados não fixa algoritmo/rotação de chave nem lista de revogação operacional | média | definir na implementação do runner; teste de recusa de pacote revogado |
| L2 | Detecção de `DetectedMin/MaxPstSizeMb` sem método de sondagem **sem efeito colateral** especificado por família | média | especificar sondagem read-only por família no laboratório (test-plan T1/T5) |
| L3 | Parsing do relatório de exportação varia por versão; formato canônico `EvExportReport` ainda não tem esquema | média | definir esquema + testes de contrato do parser por família (T10) |
| L4 | Modo assistido depende de passos manuais do operador; risco de erro humano na custódia | média | roteiro guiado + validação/hash obrigatórios no retorno (operational-runbook) |
| L5 | `MailboxItem`/permissões do EX não se aplicam ao EV, mas permissões mínimas da conta de serviço EV não estão enumeradas por família | baixa | enumerar no precheck por família |
| L6 | Concorrência entre ondas no mesmo Vault Store (locks/quotas) não detalhada | baixa | política de concorrência na implementação do orquestrador |

Nenhuma lacuna **invalida a decisão arquitetural**; todas são de
**implementação/certificação**, cobertas pelo ciclo `planejado → em
laboratório → certificado` da matriz de compatibilidade.

## 5. Segurança (revisão Segurança)

- Superfície de execução no ambiente do cliente restrita a scripts
  assinados + allowlist; **sem** execução arbitrária remota — **adequado**.
- Conector outbound-only, sem inbound no cliente (runbook §15) — **adequado**.
- Recomendações: fixar rotação/revogação de assinatura (L1); garantir que
  o relatório de capacidades (evidência) e os relatórios de exportação não
  vazem PII; incluir os itens L1–L4 no escopo de pen-test/hardening antes
  de certificar qualquer família.

## 6. Recomendação

As especificações de `docs/ev/` **implementam fielmente** a decisão do
ADR-0013 e estão **tecnicamente sólidas** para servir de base à
implementação. Recomenda-se **aceitar o ADR-0013**, com as lacunas L1–L6
registradas como **condições de certificação** (não de aceitação da
decisão): elas são resolvidas por família no laboratório, não reabrem a
arquitetura.

A **aceitação formal** é ato do Decision Owner (Vinicius Miranda). O
ADR-0013 permanece `proposto` até esse registro; a aceitação pode ocorrer
no mesmo PR ou em PR de aceite separado (esta evidência já no `main`),
conforme a [matriz](../gate-closure-matrix.md).
