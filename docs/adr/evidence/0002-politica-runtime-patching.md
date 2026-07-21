# Política de runtime, atualização e patching — gate do ADR-0002

Evidência requerida pelo gate do
[ADR-0002](../0002-dotnet-10-lts-e-politica-de-atualizacao.md) (.NET 10 LTS
e política de atualização de dependências).

- **Tipo:** política de engenharia (revisão Dev + Segurança)
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge, sob direção
  do Decision Owner
- **Data:** 2026-07-21
- **Natureza:** define a política que sustenta a decisão do ADR-0002. Não é
  a aceitação formal (ato do Decision Owner). Revisão assumidamente **não
  independente** (mesma engenharia); competência Dev + Segurança
  exercida/aceita pelo Decision Owner até haver revisor distinto.

## 1. Runtime

- **Runtime alvo:** **.NET 10 LTS** para `Control.Api`, `Control.Orchestrator`
  e workers (capa do runbook).
- **Fixação de versão:** SDK e runtime pinados via `global.json`
  (`rollForward` controlado); imagem/base pinada por digest. Sem
  `latest`/floating em produção.
- **Janela de suporte:** acompanhar o ciclo **LTS** da Microsoft; planejar o
  upgrade para a próxima LTS **antes** do fim de suporte da vigente
  (upgrade é trabalho planejado, não emergencial).
- **Workers Windows:** a engine roda em **Windows Server suportado e
  patchado** (runbook §34).

## 2. Atualização de dependências (supply chain)

- **Gerenciamento central de pacotes** (runbook §10.2):
  `Directory.Packages.props` com `ManagePackageVersionsCentrally=true`;
  versões **exatas**, sem wildcard.
- **Restore determinístico:** lock files commitados; `dotnet restore
  --locked-mode` no CI (runbook §37.3).
- **Fluxo de atualização:** toda mudança de versão entra por **pull
  request** (idealmente automatizado), revisada, com CI verde.
- **Verificação de vulnerabilidade:** `dotnet list ... package --vulnerable
  --include-transitive` e dependency scan no pipeline; **SBOM**
  (CycloneDX/SPDX) por build (runbook §37.1, itens 100–101).
- **Proveniência:** builds determinísticos, artifacts assinados, publicação
  só em registry privado (§37.1, itens 96/104/105).

## 3. Patching de SO e imagens de worker (runbook §34)

- **Cadência base:** imagem **imutável reconstruída mensalmente**.
- **CVE crítico:** reconstrução **fora de cadência** para CVE crítico
  (patch prioritário).
- **Hardening associado:** Defender for Endpoint + tamper protection;
  WDAC/App Control allowlist; SMBv1/TLS legado desabilitados; worker
  **reimaginado** após job de alto risco ou manipulação de SAS.

## 4. SLA de patch de segurança (proposto)

| Severidade | Dependência (.NET/pacote) | SO/imagem de worker |
| --- | --- | --- |
| Crítica | PR de atualização + deploy prioritário | reconstrução fora de cadência |
| Alta | dentro do ciclo de release corrente | próxima reconstrução (≤ mensal) |
| Média/Baixa | backlog priorizado | cadência mensal |

Os prazos numéricos definitivos são ratificados com a operação (Parte V/VI);
esta tabela fixa a **política de prioridade**, não números contratuais.

## 5. Verificação (onde a política é aplicada)

- CI de PR (§37.1): `--locked-mode`, SAST, secret scanning, dependency e
  container scan, SBOM, build determinístico — falham o merge se violados.
- Promoção (§37.2): build uma vez, promover o mesmo digest; prod exige dois
  aprovadores e rollback plan.
- Comandos canônicos: runbook §37.3.

## 6. Riscos e condições

| # | Item | Encaminhamento |
| --- | --- | --- |
| R1 | Fim de suporte da LTS vigente sem upgrade planejado | item de roadmap com data-alvo antes do EOL |
| R2 | Pacote crítico sem correção upstream | avaliar mitigação/substituição; registrar exceção com prazo |
| R3 | Deriva entre imagem de worker e baseline de hardening | reconstrução mensal + verificação de conformidade no pipeline |

Nenhum risco invalida a decisão; são **condições de operação contínua**.

## 7. Recomendação

A política de runtime, atualização e patching **sustenta e operacionaliza**
a decisão do ADR-0002, aderente ao runbook (§10.2, §34, §37).
Recomenda-se **aceitar o ADR-0002**, com R1–R3 como condições de operação
contínua.

A **aceitação formal** é ato do Decision Owner (Vinicius Miranda); o
ADR-0002 permanece `proposto` até esse registro. O flip para `aceito`
ocorrerá **neste mesmo PR** após a autorização — um único CI e um único
merge, conforme o processo definido.
