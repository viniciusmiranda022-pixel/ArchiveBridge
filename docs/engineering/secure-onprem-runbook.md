<!-- Consolidação de engenharia. Fonte de autoridade: os ADRs em docs/adr/ e o runbook em docs/runbook/. Este documento NÃO altera o status de nenhum ADR e NÃO introduz decisões novas. -->

# Runbook de Engenharia Segura e Arquitetura On-Premises

Handbook de engenharia do **ArchiveBridge** para o período **anterior ao
scaffolding**. Consolida, num único lugar, o modelo de **arquitetura
on-premises** e as **práticas de engenharia segura** já decididas nos ADRs e
no [runbook de engenharia](../runbook/README.md). É um documento de
**consolidação e referência** — a **autoridade** é sempre o ADR citado.

> [!IMPORTANT]
> Este runbook **não altera o status de nenhum ADR** e **não introduz
> decisões novas**. Onde um ADR está `proposto`, o item aparece marcado como
> **pendente** e **não** deve ser tratado como decisão fechada. O scaffolding
> .NET permanece **bloqueado** até o fechamento dos gates obrigatórios (ver
> [matriz de fechamento](../adr/gate-closure-matrix.md)).

## Legenda de estado das decisões

| Estado | Significado |
| --- | --- |
| **aceito** | ADR aceito pelo Decision Owner; vigente |
| **proposto** | ADR em revisão; conteúdo aqui é **provisório**, não fechado |

## Índice de autoridade (ADRs)

| ADR | Tema | Estado |
| --- | --- | --- |
| [ADR-0001](../adr/0001-monolito-modular-e-workers-isolados.md) | Monólito modular + workers isolados | **aceito** |
| [ADR-0002](../adr/0002-dotnet-10-lts-e-politica-de-atualizacao.md) | .NET 10 LTS e política de atualização | **aceito** |
| [ADR-0003](../adr/0003-azure-sql-e-service-bus-premium.md) | Persistência e execução durável **on-premises** | **aceito** |
| [ADR-0007](../adr/0007-graph-fts-bloqueado.md) | Graph condicional; rota PST/EV → FTS bloqueada | **aceito** |
| [ADR-0013](../adr/0013-exportacao-ev-multiversao.md) | Exportação EV multiversão por capability discovery | **aceito** |
| [ADR-0006](../adr/0006-purview-adapter-ga-inicial.md) | Purview Network Upload como adapter GA inicial | proposto |
| [ADR-0008](../adr/0008-isolamento-por-tenant-e-projeto.md) | Isolamento por tenant/projeto (identidade, segredos, rede) | proposto |
| [ADR-0005](../adr/0005-libpff-validador-independente.md) | libpff como verificador independente | proposto |

## 1. Baseline arquitetural on-premises (ADR-0003, aceito)

O ArchiveBridge é **instalado e operado na infraestrutura do cliente**: banco,
aplicação, workers, logs, segredos, storage e backups permanecem no ambiente
do cliente. **Não há Control Plane SaaS** e **não há dependência obrigatória
de Azure PaaS**. O **Microsoft 365 é o destino externo** da migração.

- **Componentes:** Control Plane (ASP.NET Core em IIS/Windows Service);
  orquestração (serviço .NET local); **SQL Server local** como sistema de
  registro; **workers Windows Services** isolados; PSTs/artefatos em
  **NTFS/NAS/SMB**; logs em Event Log/arquivos estruturados/SIEM local.
- **Perfis de implantação:** instalação básica; produção padrão; **alta
  disponibilidade (opcional)** com SQL Server Always On/cluster. **HA é opção
  de implantação, não dependência de Azure.**
- **Azure e brokers externos** só podem existir como **adapters opcionais
  futuros**, nunca como dependência obrigatória.

## 2. Persistência e execução durável (ADR-0003, aceito)

O SQL Server local é o sistema de registro de estados, locks, leases,
checkpoints, outbox, inbox, auditoria e reconciliação. A execução assíncrona
usa **fila durável em SQL Server** (sem broker no release inicial). O contrato
de correção da fila é obrigatório:

- **aquisição atômica** de trabalho; `rowversion` gerado pelo SQL Server
  (nunca atribuído);
- **fencing por `owner_worker + lease_epoch`** — o `row_ver` **não** é o token
  de fencing (muda a cada update); perda de lease invalida o worker;
- jobs com possível **efeito externo** **nunca** voltam automaticamente a
  `PENDING` — vão a `RECOVERY_REQUIRED`/`RECONCILING` com consulta ao provedor;
- **ledger `external_operations`** (`INTENT/SUBMITTED/CONFIRMED/AMBIGUOUS/FAILED`)
  porque **não há transação distribuída** com Purview/Graph/EXO — **ambíguo
  nunca repete automaticamente**;
- **failover HA exige commit síncrono zero-data-loss** para o ledger, com
  reconciliação por **chave visível no provedor**;
- **anti-starvation** por aging/quota; retry/backoff/DLQ; **teste de
  concorrência multi-worker** obrigatório antes de produção.

> Chave de operação: a `operation_key` é **determinística e gerada pelo
> ArchiveBridge antes do efeito externo**; o ID do provedor (ex.: Purview job
> ID) só existe **após** a criação e **não** é a chave (ver §7).

## 3. Identidade e isolamento (ADR-0008, proposto)

> [!WARNING]
> ADR-0008 está `proposto` (revisão de Segurança/DPO pendente). Os itens abaixo
> são **provisórios**.

- **Isolamento por tenant e projeto:** `tenant_id` em todas as tabelas; índices
  liderados por tenant; **Row-Level Security do SQL Server** como **defesa em
  profundidade** — a **autorização permanece na aplicação**, com escopo
  tenant/projeto explícito; **nenhuma consulta depende só do session context**
  do SQL; testes cross-tenant validam **aplicação + SQL**.
- **Uma identidade de serviço Windows por workload local** (Control/EV/PST/
  Upload/Recon/Evidence): **gMSA ou virtual service account**, **nunca Domain
  Admin**, **sem secret compartilhado**, RBAC mínimo, **segregação de funções**
  (quem prepara não aprova; quem aprova não altera artefato).
- **Cada operação externa tem identidade própria** e permission set mínimo —
  **não** há uma identidade genérica "M365". Purview (operador humano), Purview
  Approver (humano), M365 Precheck App, reconciliação e Graph FTS App
  (condicional, bloqueado) **não compartilham identidade por padrão**. O acesso
  programático ao destino usa **app-only CBA** com role mínimo.
- **Testes de isolamento e scaffolding:** não há código antes do scaffolding;
  os testes de arquitetura, autorização e isolamento cross-tenant **existem no
  primeiro PR de scaffolding** e são obrigatórios desde o primeiro módulo que
  persista ou consulte dados escopados por tenant.

## 4. Segredos e material criptográfico (ADR-0008, proposto)

- **Mecanismo de segredos on-premises:** **perfil de nó único = DPAPI**;
  **perfil HA de segredos = `BLOCKED_PENDING_EVIDENCE`** até um mecanismo
  multi-nó concreto (key ring por certificado, store corporativo homologado,
  HSM ou secrets management do cliente) ser escolhido e certificado.
- **Certificate Store** do Windows para certificados; **ACLs exclusivas**.
- **HMAC por tenant, versionadas** (`keyVersion` persistida; rotação preserva
  fingerprints antigos).
- **Assinatura de evidência** com **chave não exportável** (TPM/HSM local).
- **Renovação de certificados** automatizada, alarmes 30/14/7 dias.
- **Redaction central** de toda telemetria (remove query string, Authorization,
  cookies, UPN/SMTP em claro, UNC real, subject/body/attachment name), com
  **testes de canary** que falham o build.

## 5. Rede e conectividade (ADR-0003/ADR-0008)

- **Nenhuma entrada proveniente da internet.**
- **Egress externo somente HTTPS 443** aos endpoints Microsoft autorizados
  (Entra ID, Exchange Online, Graph, Purview e o Azure Storage temporário do
  Purview).
- **Fluxos internos** (Control Plane ↔ SQL; workers ↔ SQL; workers ↔ NAS/SMB;
  worker ↔ Enterprise Vault; mTLS entre componentes; navegadores internos ↔
  Portal) usam **portas explicitamente registradas na matriz de fluxos e
  portas**, com **segmentação por firewall/VLAN** do cliente. O "somente 443"
  é regra de **egress externo**, não dos fluxos internos aprovados.

## 6. Destinos Microsoft 365

### 6.1 Purview Network Upload — adapter GA inicial (ADR-0006, proposto)

- Purview/M365 é **adapter de destino externo**, não dependência de
  hospedagem: sem assinatura Azure do cliente; o container `ingestiondata` é
  staging temporário provido pela Microsoft, alcançado por **URL SAS**.
- O produto prepara parts localmente, gera o **CSV mapping oficial** e
  **transporta via AzCopy homologado a partir de um upload worker
  on-premises**; a **criação/início do import job** permanece **workflow humano
  no portal Purview**.
- **Capacity gate** obrigatório; **bloqueio de >100 GB no mesmo archive**
  (`MICROSOFT_ASSESSMENT_REQUIRED`); auto-expanding archive **não** é bypass.
- **Exceção controlada e restrita — SAS no argv do AzCopy:** o fluxo
  documentado pela Microsoft usa `azcopy copy "<Source>" "<SAS URL>"`; a URL
  SAS é tratada como credencial. A exceção vale **somente** para este adapter,
  com **controles compensatórios** (worker/identidade exclusivos, sem usuário
  interativo, admin JIT, transcript/command history off, sem gravação do
  comando completo, sanitização de logs/telemetria, encerramento após upload,
  destruição da cópia local, **teste automático de vazamento do SAS**). **Não**
  é flexibilização geral do SSDLC.
- **Sem revogação remota prometida:** o produto destrói cópias locais e bloqueia
  reutilização interna; **validade/revogação do SAS no serviço seguem as
  capacidades do Purview** (sem capability evidence, sem promessa de revogação
  remota).
- **Ledger `external_operations`:** `operation_key` determinística gravada em
  `INTENT` **antes** do efeito; **nome planejado do job** usado no portal;
  `provider_operation_id` registrado **após** a criação; reconciliação por
  **nome planejado + provider id**; ambíguo nunca repete automaticamente.

### 6.2 Graph Mailbox Import/Export — condicional (ADR-0007, aceito)

O Graph permanece **adapter condicional** (`GraphFtsTargetAdapter`); **apenas**
a rota **PST/EV → FTS** fica em `GraphFtsImportFromPstEv =
BLOCKED_PENDING_EVIDENCE`. **Não** é bloqueio global do Graph. Promoção pelo
ciclo `BLOCKED_PENDING_EVIDENCE → CANDIDATE → CERTIFIED → ENABLED`; novo ADR só
se mudar contrato, segurança ou arquitetura. Ver o
[catálogo de adapters de destino](../adr/target-adapter-catalog.md).

## 7. Engine PST e validação independente

### 7.1 Exportação Enterprise Vault multiversão (ADR-0013, aceito)

O EV **extrai e segmenta os PSTs na origem** (`Export-EVArchive -MaxPSTSizeMB`),
por **capability discovery** e **adapters por família** (assinados,
certificados); modo assistido quando não há adapter certificado. Aspose saiu do
caminho crítico. As lacunas L1–L6 são condições de certificação por família.

### 7.2 Validador independente libpff (ADR-0005, proposto)

> [!WARNING]
> ADR-0005 está `proposto` (parecer jurídico LGPL pendente).

- libpff é a **segunda engine de verificação**, **somente leitura**, contra o
  resultado do handle primário (rota EV ou ingestão de PST existente). **Não**
  é writer/splitter nem repara; seus tipos **nunca atravessam** `IPstEngine`.
- **Perfil Windows:** processo isolado sob gMSA de menor privilégio; **NTFS ACL**
  + **WDAC/App Control** impedem execução em diretórios de dados; **AppLocker**
  complementar; **sem rede**. O termo `noexec` aplica-se apenas a Linux/container.
- **`pffinfo` é a ferramenta padrão de inspeção**; **`pffexport` apenas em
  laboratório/validação aprovada**, em scratch efêmero criptografado; conteúdo
  extraído **não** entra automaticamente na evidência.
- **Contrato do processo** (`LibpffValidationRequest`/`LibpffValidationResult`) e
  **plano de compatibilidade** (encoding, locale, exit codes, timeout,
  corrupção, ANSI/Unicode, PST grande, CPU/RAM, cancellation, ausência de rede,
  **hash antes/depois**) na evidência do ADR-0005.
- **Licença:** libpff é **LGPL-3.0-or-later**, upstream **alpha** — build
  escolhido deve ser **fixado (commit/versão/SHA-256)** e **certificado**; o
  **parecer jurídico externo é obrigatório** (sem exceção de bootstrap).

## 8. Hardening dos workers Windows (runbook §34)

Windows Server suportado e patchado; imagem imutável reconstruída
mensalmente/para CVE crítico; Defender for Endpoint + tamper protection;
**WDAC/App Control allowlist**; PowerShell Constrained Language/JEA; **RDP
desabilitado** (break-glass por JIT/PIM); Credential Guard, BitLocker, Secure
Boot, vTPM; SMBv1/TLS legado desabilitados; serviços em **gMSA/virtual service
account** (nunca Domain Admin); scratch com ACL exclusiva sem execução;
**reimage apenas após incidente/comprometimento/suspeita** (não como rotina).

## 9. Malware e conteúdo hostil (runbook §35)

PST e anexos são **dados não confiáveis**: sem execução de macros, scripts ou
preview; extração de anexo só quando necessária, em diretório com ACL e sem
execução, com scan e limite; HTML nunca renderizado sem sanitização; itens
detectados registram hash e disposition, **nunca redistribuídos**.

## 10. Engenharia segura: CI/CD e supply chain (runbook §37)

- **PR pipeline:** checkout por SHA; `dotnet restore --locked-mode`; SAST +
  secret scanning + IaC scanning; build Release determinístico; **unit +
  architecture tests**; **testes de autorização/isolamento cross-tenant**;
  SBOM (CycloneDX/SPDX); dependency + container scan; provenance e **assinatura**;
  publicar **somente em registry privado**.
- **Promoção:** build uma vez, promover o mesmo digest; prod exige **dois
  aprovadores**, evidência de testes, rollback plan e janela; migração de schema
  é **expand/contract**.
- **Segredos no pipeline:** nenhum segredo em variável persistente quando a
  identidade de workload resolve.

## 11. Observabilidade (runbook §39)

Logging estruturado com campos obrigatórios (timestamp UTC, tenant HMAC,
project/job/artifact/wave/attempt IDs, correlation/trace, outcome, errorCode) e
**proibidos** (subject, body, attachment name, SAS, token, path real). Métricas
de fila/worker/stage; **alertas Sev1** para hash mismatch, cross-tenant denial
e secret-leak canary.

## 12. Governança de gates e scaffolding

O código de produto (scaffolding .NET) permanece **bloqueado** até o
fechamento dos gates obrigatórios — **ADR-0001 a 0003, ADR-0005 a 0008 e
ADR-0013** — conforme a [matriz de fechamento](../adr/gate-closure-matrix.md).
Cada gate fecha com **evidência registrada + status `aceito`** no `main`; a
aceitação formal é ato exclusivo do **Decision Owner** (Vinicius Miranda).
Gates ainda **pendentes**: **ADR-0006** (validação Purview em tenant),
**ADR-0008** (revisão Segurança/DPO) e **ADR-0005** (parecer jurídico LGPL).

## Referências

- ADRs: [índice](../adr/README.md) · [matriz de fechamento](../adr/gate-closure-matrix.md) · [catálogo de adapters](../adr/target-adapter-catalog.md)
- Runbook de engenharia (migração): [índice](../runbook/README.md), Partes [II](../runbook/02-parte-ii-arquitetura.md), [III](../runbook/03-parte-iii-conectores-e-engine-pst.md), [IV](../runbook/04-parte-iv-destinos-m365.md), [V](../runbook/05-parte-v-seguranca-infra-operacao.md)
- Conector EV: [docs/ev](../ev/README.md)
