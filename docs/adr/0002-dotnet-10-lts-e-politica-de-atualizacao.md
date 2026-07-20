# ADR-0002 — .NET 10 LTS e política de atualização de dependências

- **Status:** proposto
- **Data:** 2026-07-20
- **Gate de aprovação:** segurança + plataforma
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** —

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
