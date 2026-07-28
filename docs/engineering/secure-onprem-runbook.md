<!-- Runbook de engenharia. Fonte de autoridade: os ADRs em docs/adr/ e o runbook em docs/runbook/. Este documento NÃO altera o status de nenhum ADR e NÃO introduz decisões novas. Onde um ADR está proposto, o conteúdo é provisório. -->

# Runbook de Engenharia Segura e Arquitetura On-Premises

Runbook completo de engenharia do **ArchiveBridge** para o período **anterior
ao scaffolding**. Reúne princípios de engenharia, arquitetura on-premises e
o ciclo de desenvolvimento seguro. O [overview](overview.md) é o resumo; este
documento é a versão detalhada (seções **0 a 22**).

> [!IMPORTANT]
> Este runbook **não altera o status de nenhum ADR** e **não introduz decisões
> novas** — consolida decisões já registradas. Onde um ADR está `proposto`, o
> conteúdo é **provisório**. O scaffolding .NET permanece **bloqueado** até o
> fechamento dos gates obrigatórios (ver [matriz de fechamento](../adr/gate-closure-matrix.md)).

## Estado das decisões citadas

| Estado | ADRs |
| --- | --- |
| **aceito** | [0001](../adr/0001-monolito-modular-e-workers-isolados.md), [0002](../adr/0002-dotnet-10-lts-e-politica-de-atualizacao.md), [0003](../adr/0003-azure-sql-e-service-bus-premium.md), [0007](../adr/0007-graph-fts-bloqueado.md), [0013](../adr/0013-exportacao-ev-multiversao.md) |
| **proposto** (provisório) | [0005](../adr/0005-libpff-validador-independente.md), [0006](../adr/0006-purview-adapter-ga-inicial.md), [0008](../adr/0008-isolamento-por-tenant-e-projeto.md) |

## 0. Propósito, escopo e como usar

Este runbook orienta a engenharia **antes** de existir código. Serve para: (a)
fixar o vocabulário e os princípios; (b) descrever a arquitetura on-premises
alvo; (c) definir o **SSDLC** que o primeiro scaffolding já deve cumprir.
**Não** é especificação de implementação nem substitui os ADRs. Regra de ouro:
**a autoridade é o ADR**; onde este runbook e um ADR divergirem, vale o ADR.

## 1. Princípios de engenharia (KISS, DRY, YAGNI, SOLID)

- **KISS** — a solução mais simples que satisfaz o requisito e os invariantes
  de segurança. Ex.: fila durável em **SQL Server** em vez de broker no release
  inicial ([ADR-0003](../adr/0003-azure-sql-e-service-bus-premium.md)).
- **DRY** — uma única fonte de verdade por conceito (contratos, taxonomia de
  erros, redaction). Duplicação de regra de segurança é defeito.
- **YAGNI** — não projetar perfis/adapters que não serão implementados agora
  (ex.: perfil Azure entra só por **ADR futuro** — ADR-0008).
- **SOLID** — SRP por módulo/worker; OCP via **ports & adapters** (§3); LSP nos
  contratos de adapter; **ISP** (`IPstEngine`, `ITargetIngestor`,
  `IEvExportAdapter` enxutos); **DIP** — o domínio depende de abstrações, nunca
  de infra concreta ([§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência)).

## 2. DDD e modelo de domínio

- **Linguagem ubíqua:** projeto, onda (wave), part, artifact, lease, attempt,
  custody event, disposition — os mesmos termos no código, nos logs e na
  evidência.
- **Agregados e invariantes** ([§11](../runbook/02-parte-ii-arquitetura.md#11-modelo-de-domínio-e-invariantes)):
  o estado só muda por **transições válidas**; transições proibidas falham o
  build/os testes (§5).
- **Resultados normalizados:** tipos de bibliotecas externas (Aspose, libpff)
  **nunca** atravessam as interfaces do domínio ([§18.2](../runbook/03-parte-iii-conectores-e-engine-pst.md#182-interface-do-adapter)).

## 3. Arquitetura hexagonal (ports & adapters)

- **Domínio no centro**, sem dependência de infraestrutura; **ports** (interfaces)
  para fora; **adapters** implementam ports (SQL, EV, Purview, Graph, libpff).
- **Regra de dependência** ([§8.1](../runbook/02-parte-ii-arquitetura.md#81-regras-de-dependência)):
  um módulo **não** referencia a infraestrutura de outro; dependências apontam
  para dentro. **Architecture tests** no CI falham o build ao violar a regra.
- **Adapters evoluíveis:** destinos M365 atrás de `ITargetIngestor`; trocar/
  adicionar adapter não altera o domínio ([catálogo](../adr/target-adapter-catalog.md)).

## 4. Modularização e workers isolados (ADR-0001, aceito)

Monólito **modular** com **workers isolados** por responsabilidade (EV, PST,
Upload, Recon, Evidence). Cada worker é um **Windows Service** com identidade
própria (§8), sem secret compartilhado, falha contida (blast radius reduzido).

## 5. Estado e máquina de estados

- Estados de job/part/attempt explícitos; **máquina de estados** com transições
  válidas e **transições proibidas** ([§11.3–§11.4](../runbook/02-parte-ii-arquitetura.md#113-máquina-de-estados)).
- Jobs com **efeito externo** não voltam automaticamente a `PENDING`; vão a
  `RECOVERY_REQUIRED`/`RECONCILING` (§6). Part sem `VALIDATED` **não** é
  consumível.

## 6. Concorrência e execução durável (ADR-0003, aceito)

- **Fila durável em SQL Server** (sem broker inicial); aquisição atômica de
  trabalho; `rowversion` gerado pelo SQL (nunca atribuído).
- **Fencing por `owner_worker + lease_epoch`** — `row_ver` **não** é token de
  fencing (muda a cada update); perda de lease invalida o worker.
- **Ledger `external_operations`** (`INTENT/SUBMITTED/CONFIRMED/AMBIGUOUS/FAILED`):
  sem transação distribuída com Purview/Graph/EXO; **ambíguo nunca repete
  automaticamente**. `operation_key` **determinística antes do efeito**; ID do
  provedor só existe após criação e **não** é a chave (ADR-0006).
- **Failover HA:** commit síncrono **zero-data-loss** para o ledger; reconciliação
  por chave visível no provedor. **Anti-starvation** por aging/quota; DLQ;
  **teste de concorrência multi-worker obrigatório antes de produção**.

## 7. Filesystem seguro

- Containers por ciclo de vida (`landing`, `work`, `parts`, `quarantine`,
  `evidence`, `reports` — [§33](../runbook/05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida))
  em **NTFS/NAS/SMB** com **ACL exclusiva**, encryption at rest, WORM para
  `evidence`.
- **Path traversal:** canonical path + raiz allowlisted; nomes sanitizados.
- **Sem execução em diretórios de dados:** NTFS ACL + **WDAC/App Control**
  (AppLocker complementar); `noexec` só em Linux/container.
- **Quarentena** para corrupção/itens hostis; nada é descartado silenciosamente.

## 8. Identidade, segredos e isolamento (ADR-0008, proposto)

> [!WARNING]
> ADR-0008 `proposto` (revisão Segurança/DPO pendente) — provisório.

- **Isolamento por tenant/projeto:** `tenant_id` em todas as tabelas; **RLS do
  SQL Server** como **defesa em profundidade** (autorização na aplicação;
  nenhuma consulta depende só do session context); testes cross-tenant validam
  aplicação + SQL.
- **Identidade por workload e por operação:** gMSA/virtual service account por
  worker local; **cada operação externa com identidade própria** — sem
  identidade genérica "M365"; acesso programático por **app-only CBA** com role
  mínimo.
- **Segredos:** **DPAPI** (nó único); **HA de segredos `BLOCKED_PENDING_EVIDENCE`**
  até mecanismo multi-nó concreto e certificado; Certificate Store; ACLs; HMAC
  por tenant versionadas; assinatura com chave não exportável; **redaction
  central** com canaries.
- **SAS do Purview:** custódia on-premises; destruição de cópias locais após
  upload; **sem promessa de revogação remota** (ADR-0006); **reimage do worker
  só após incidente/comprometimento/suspeita** — não como rotina.

## 9. Rede e conectividade (ADR-0003)

- **Nenhuma entrada da internet.** **Egress externo somente HTTPS 443** aos
  endpoints Microsoft autorizados. **Fluxos internos** (SQL, SMB/NAS, EV, mTLS,
  Portal) em **portas registradas na matriz de fluxos e portas**, com
  segmentação por firewall/VLAN.

## 10. Destinos Microsoft 365 (ADR-0006 proposto, ADR-0007 aceito)

- **Purview Network Upload** é o adapter GA inicial (proposto): prepara parts
  localmente, gera o CSV oficial, transporta por **AzCopy** a partir de worker
  on-premises; criação/início do job é **workflow humano no portal**; **capacity
  gate** e **bloqueio >100 GB** mantidos; **exceção controlada do SAS no argv**
  restrita a este adapter. A validação separa **Gate A (pré-código, bloqueia
  aceitação)** de **Gate B (contrato de implementação, antes de produção)**.
- **Graph** permanece **condicional**; a rota **PST/EV → FTS** fica
  `BLOCKED_PENDING_EVIDENCE` — não é bloqueio global (ADR-0007).

## 11. Engine PST e validação independente (ADR-0013 aceito, ADR-0005 proposto)

- **EV multiversão** extrai e segmenta PSTs na origem por capability discovery,
  com adapters por família assinados/certificados (ADR-0013).
- **Validador libpff** (proposto): **segunda engine somente leitura**; **pffinfo
  padrão**, **pffexport só em lab**; processo isolado (gMSA, NTFS ACL,
  WDAC); tipos nunca atravessam `IPstEngine`; **LGPL-3.0-or-later** com parecer
  jurídico e artefato fixado pendentes (ADR-0005).

## 12. Malware e conteúdo hostil (runbook §35)

Dados não confiáveis: sem macros/scripts/preview; extração de anexo só quando
necessária, em diretório com ACL e sem execução, com scan; HTML nunca renderizado
sem sanitização; itens hostis registram hash e disposition, **nunca
redistribuídos**.

## 13. SSDLC (ciclo de desenvolvimento seguro)

Ordem: **requisito → threat model (§14) → design com security controls →
implementação → testes (§15–16) → análise estática/dinâmica (§17) → revisão e
DoD (§20) → CI/CD (§18) → operação**. Segurança é **shift-left**: o primeiro
PR de scaffolding já traz architecture tests e testes de isolamento
cross-tenant (§8). Nenhum segredo em código ou em variável de pipeline
persistente.

## 14. Threat modeling

- **STRIDE por feature** e por ativo ([§30](../runbook/05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos));
  cada ameaça mapeia a um controle obrigatório e a um teste.
- **Por engajamento:** cada implantação valida a matriz de fluxos/portas e a
  segmentação do cliente. Ameaças-chave: cross-tenant, SAS em log, PST hostil,
  elevação de privilégio entre workers, path traversal, supply-chain.

## 15. Estratégia de testes

- **Unit** (domínio/invariantes); **architecture tests** (regra de dependência,
  tipos externos não vazam); **contract tests** por adapter (EV, Purview, Graph,
  libpff — request/result); **authorization/isolation tests** cross-tenant
  (canaries de RLS); **integration** em recursos efêmeros; **concorrência
  multi-worker** (fencing + starvation); **chaos** (crash/timeout/ambíguo);
  **e2e** com PST **sintético** (sem PII).
- **Determinismo:** sem dependência de relógio/ordenação implícita; PSTs de
  teste sintéticos e versionados.

## 16. Cobertura e mutation testing

- **Cobertura mínima** nos módulos críticos (domínio, fila, ledger, redaction,
  capacity gate) — cobertura é piso, não meta.
- **Mutation testing** nos módulos críticos: um teste que não mata mutantes não
  protege o invariante. Metas de mutation score definidas por módulo crítico.

## 17. Análise estática e dinâmica: SAST, DAST, SCA, SBOM

- **SAST** + **secret scanning** + **IaC scanning** no PR; falha bloqueia merge.
- **SCA** (dependency vulnerability scan, incl. transitivas) e **container scan**.
- **SBOM** (CycloneDX/SPDX) gerado e assinado por build.
- **DAST** contra o Portal/superfícies expostas em ambiente de teste antes de
  promover; pen-test antes de habilitar qualquer rota sensível.

## 18. CI/CD e supply chain (runbook §37)

- **PR pipeline:** checkout por SHA; `dotnet restore --locked-mode`; SAST/secret/
  IaC scan; build Release **determinístico**; unit + architecture +
  authorization tests; SBOM; SCA; container scan; provenance + **assinatura**;
  publicar **somente em registry privado**.
- **Promoção:** build uma vez, promover o **mesmo digest**; prod exige **dois
  aprovadores**, evidência de testes, rollback plan e janela; schema por
  **expand/contract**.

## 19. Vertical slices e fluxo de entrega

- Entregar por **fatia vertical** (domínio → port → adapter → teste →
  observabilidade) que atravessa as camadas e entrega valor verificável, em vez
  de camadas horizontais incompletas.
- Cada slice nasce com seus testes (§15), sua evidência e seus controles de
  segurança — não há slice "sem teste, adiciono depois".

## 20. Code review e Definition of Done

- **Code review** obrigatório por PR; foco em invariantes de segurança, regra
  de dependência, taxonomia de erros, ausência de segredos e de tipos externos
  vazando o domínio. **CODEOWNERS** aplica-se.
- **Definition of Done:** requisito atendido; testes (unit/arch/authorization/
  contract quando aplicável) verdes; cobertura e mutation nos módulos críticos;
  SAST/SCA/secret scan limpos; SBOM gerado; observabilidade e redaction
  presentes; docs/ADR atualizados; **sem TODO de segurança em aberto**.

## 21. Stop-the-line e resposta a incidentes

- **Stop-the-line:** hash mismatch, cross-tenant denial e **secret-leak canary**
  são **Sev1** e **param a linha** — nenhuma onda nova até conter. Nunca
  "seguir e corrigir depois" em falha de custódia ou vazamento.
- **Incidente de segredo em log** (runbook §42.6): revogar/rotacionar, bloquear
  o log sem destruir evidência legal, atualizar redactor/testes, **reconstruir
  o worker**, comunicar conforme LGPD.

## 22. Anti-patterns (o que NÃO fazer)

- Segredo em código, em variável de pipeline persistente ou em log/telemetria.
- SAS fora da **exceção controlada** do adapter Purview; imprimir a command line
  do AzCopy.
- Identidade genérica "M365"; secret compartilhado entre workers; usar Domain
  Admin.
- Confiar **apenas** no RLS para autorização; consulta que depende só do session
  context do SQL.
- Usar `provider_operation_id` como chave do ledger; reprocessar automaticamente
  operação **ambígua**; reusar a mesma pasta de upload para projetos diferentes.
- Tratar tipos de Aspose/libpff dentro do domínio; usar libpff como writer;
  `pffexport` fora de laboratório.
- Editar o CSV do Purview "só uma linha" no Excel; iniciar segunda importação
  para o mesmo request; remover Retention Hold automaticamente.
- Renderizar HTML/anexos sem sanitização; executar macro/script de PST.
- Exigir **código inexistente** como evidência prévia de aceitação de ADR;
  iniciar scaffolding com gate obrigatório em aberto.
- Marcar PR de ADR como Ready/mergear sem o flip de aceite e a autorização do
  Decision Owner.

## Referências

- ADRs: [índice](../adr/README.md) · [matriz de fechamento](../adr/gate-closure-matrix.md) · [catálogo de adapters](../adr/target-adapter-catalog.md)
- Runbook (migração): [índice](../runbook/README.md) · Partes [II](../runbook/02-parte-ii-arquitetura.md), [III](../runbook/03-parte-iii-conectores-e-engine-pst.md), [IV](../runbook/04-parte-iv-destinos-m365.md), [V](../runbook/05-parte-v-seguranca-infra-operacao.md)
- Overview: [overview.md](overview.md) · Conector EV: [docs/ev](../ev/README.md)
