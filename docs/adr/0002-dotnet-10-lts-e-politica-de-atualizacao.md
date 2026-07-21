# ADR-0002 — .NET 10 LTS e política de atualização de dependências

- **Status:** aceito pelo Decision Owner em 2026-07-21; vigência no
  repositório a partir do merge deste PR de aceite.
- **Data:** 2026-07-20 (proposto) · 2026-07-21 (aceito pelo Decision Owner)
- **Gate de aprovação:** Decision Owner + revisão Dev + Segurança
- **Substitui / substituído por:** —

## Registro de aceitação

- **Decision Owner:** Vinicius Miranda — **decisão de aceitação em
  2026-07-21**. Vigência/publicação: a partir do merge deste PR.
- **Revisão executada (Dev + Segurança):** política de runtime, atualização
  e patching em
  [`evidence/0002-politica-runtime-patching.md`](evidence/0002-politica-runtime-patching.md)
  (Evidence Owner: Engenharia), **publicada pelo PR #12**. Não havendo
  revisor distinto, a competência é exercida/aceita pelo Decision Owner
  (exceção de bootstrap na [matriz](gate-closure-matrix.md)).
- **Condições obrigatórias de operação contínua (não de aceitação):** R1
  (EOL da LTS vigente sem upgrade planejado), R2 (pacote crítico sem
  correção upstream), R3 (deriva entre imagem de worker e baseline de
  hardening) permanecem obrigatórias.
- **Fixação de versão vigente:** distinção mantida entre **SDK**
  (`global.json`, versão + `rollForward`), **família do runtime** (Target
  Framework `net10.0`) e **runtime implantado** (modelo de publicação +
  imagem imutável — container por digest, VM por versão/ID, *self-contained*
  vs *framework-dependent*); sem `latest`/floating em produção.
- **Fluxo de fechamento (dois PRs):** evidência publicada pelo PR #12 (ADR
  então `proposto`); flip para `aceito` neste PR — modalidade de PR de
  aceite separado prevista na [matriz](gate-closure-matrix.md).

## Contexto

A capa do runbook fixa o runtime em **.NET 10 LTS; workers Windows isolados**. A [§10.2 Gerenciamento central de pacotes](../runbook/02-parte-ii-arquitetura.md#102-gerenciamento-central-de-pacotes) exige `ManagePackageVersionsCentrally`, versões exatas, lock files, restore determinístico e atualização por pull request automatizado, sem wildcards em produção. A engine PST roda em workers Windows por compatibilidade de biblioteca (§18).

## Decisão

Adotar **.NET 10 LTS** como runtime de `Control.Api`, `Control.Orchestrator` e workers. Os workers que executam a engine PST são **serviços Windows isolados**. O gerenciamento de pacotes é **central** (`Directory.Packages.props` com `ManagePackageVersionsCentrally=true`): versões exatas e revisadas, lock files commitados, `dotnet restore --locked-mode`, sem wildcard. Atualizações de dependência entram exclusivamente por PR (idealmente automatizado), com SBOM e scanning (ver Parte V, §37).

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Canal STS (não-LTS) | recursos mais novos antes | janela de suporte curta, upgrades forçados frequentes | conflita com estabilidade de plataforma de produção |
| .NET Framework clássico | legado consolidado | fim de inovação, só Windows, sem recursos modernos | inadequado para plano de controle multiplataforma |
| Runtimes mistos por serviço | flexibilidade pontual | matriz de suporte e segurança fragmentada | aumenta superfície de patching sem ganho |

## Consequências

- Positivas: janela de suporte LTS; política de patch de segurança previsível; restore determinístico e auditável.
- Negativas / dívidas assumidas: upgrade de major LTS periódico planejado; workers Windows amarram a engine PST a esse SO.
- Riscos e mitigação: CVE em dependência → PR de atualização automatizado + scanning no CI; drift de versão → lock files + `--locked-mode`.

## Evidências

Runbook (capa/runtime), [§10.2](../runbook/02-parte-ii-arquitetura.md#102-gerenciamento-central-de-pacotes) e Parte V (§37 CI/CD e supply chain). Confirmação do gate: parecer de segurança e plataforma no PR.
