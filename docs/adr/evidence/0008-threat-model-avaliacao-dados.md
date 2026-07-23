# Threat model on-premises e avaliação de dados — gate do ADR-0008

Evidência requerida pelo gate do
[ADR-0008](../0008-isolamento-por-tenant-e-projeto.md) (modelo de isolamento
por tenant/projeto, na baseline on-premises do
[ADR-0003](../0003-azure-sql-e-service-bus-premium.md)).

- **Tipo:** threat model (STRIDE) na realização on-premises + avaliação de
  dados/privacidade
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge
- **Revisor necessário:** Segurança/Privacidade (DPO)
- **Estado da revisão:** **pendente** — a análise abaixo sustenta a decisão
  do ADR-0008; **não** é a aceitação formal (ato do Decision Owner) nem
  substitui a revisão de Segurança/DPO.

> [!NOTE]
> Esta evidência traduz os controles das §30–§35 do runbook (redigidas em
> primitivos Azure) para a **realização on-premises** vigente (ADR-0003),
> **preservando os objetivos de controle**. Onde o runbook cita Managed
> Identity / Key Vault / private endpoint / NSG, a coluna "Controle
> on-premises" registra o mecanismo equivalente na infra do cliente.

## 1. Threat model (§30) — realização on-premises

Ativos críticos (§30.1) inalterados: conteúdo PST/EV; parts e artefatos;
SAS Purview; chaves/HMAC de fingerprint; certificados de connectors e
workloads; mapping identidade→mailbox; manifestos/evidências; permissões
Exchange/Purview/Graph; pipeline/pacotes/imagens de worker.

| Ameaça (§30.2) | Exemplo | Controle do runbook | Realização on-premises (ADR-0003/0008) |
| --- | --- | --- | --- |
| Spoofing | connector falso | mTLS, enrollment único, workload identity | mTLS + certificado por instalação; identidade de serviço **gMSA** por workload |
| Tampering | trocar part após validação | SHA-256, immutability, storage lease, WORM | SHA-256; container `parts` imutável após validação; **ACL exclusiva** NTFS/NAS; evidência WORM em storage do cliente |
| Repudiation | operador nega import | Entra identity, approval, ledger, audit | identidade Entra (destino) + **ledger `external_operations`** (ADR-0003); audit no SQL Server local / Event Log / SIEM |
| Information disclosure | SAS em log | secret redaction, no transcript, JIT worker | **redaction central** (§32.1) com canary tests; transcript desabilitado; upload worker dedicado, admin JIT |
| Denial of service | PST malformado consome RAM/IO | limites, timeout, worker isolado, quota | limites/timeout; **Windows Service isolado**; quota por tenant; scratch com ACL sem execute |
| Elevation of privilege | worker PST acessa Exchange | identidades separadas, RBAC mínimo, boundary | **gMSA distinta por workload**, sem secret compartilhado; app-only CBA com role mínimo; **segmentação de rede** do cliente |
| Cross-tenant access | API retorna job alheio | tenant key, RLS, authorization tests | `tenant_id` + **RLS do SQL Server** (nativo, on-prem) + authorization tests que falham o build |
| Supply-chain | pacote adulterado | lock file, SBOM, assinatura, scanning, registry privado | `--locked-mode`, SBOM, assinatura Authenticode, scanning (§37); registry privado do cliente |
| Queue poisoning | payload manipulado | schema, HMAC, IDs apenas, inbox | fila durável em SQL (ADR-0003); schema + inbox; payload só com IDs tipados |
| Path traversal | nome PST escreve fora da pasta | canonical path e raiz allowlisted | canonical path + raiz allowlisted; ACL de staging |

## 2. Identidade e segregação (§31) — on-premises

- **Personas** (ProjectAdmin, MigrationEngineer, MigrationApprover,
  M365Operator, Auditor, SecurityAdmin) e o princípio "quem prepara não
  aprova; quem aprova não altera artefato" — inalterados.
- **Workloads locais** (Control / EV / PST / Upload / Recon / Evidence): cada
  um com **gMSA ou virtual service account** própria, **sem secret
  compartilhado**, RBAC mínimo. O "MI" das linhas §31 é realizado on-premises
  por gMSA.
- **Cada operação externa tem identidade própria e permission set mínimo** —
  **não** há uma única identidade genérica "M365". Purview (operador humano),
  Purview Approver (humano), M365 Precheck App (leituras mínimas),
  reconciliação e Graph FTS App (condicional, bloqueado — ADR-0007) **não
  compartilham identidade por padrão**. O acesso programático ao destino
  Microsoft usa **app-only CBA** com role mínimo. Ver a matriz de identidades
  no [ADR-0008](../0008-isolamento-por-tenant-e-projeto.md).
- **Source Connector:** certificado por instalação (mTLS) — já on-premises.
- **RLS = defesa em profundidade:** a autorização permanece na Application com
  escopo tenant/projeto explícito; nenhuma consulta depende exclusivamente do
  *session context* do SQL; testes cross-tenant validam Application + SQL.

## 3. Segredos (§32) — on-premises

| Objetivo do runbook | Realização on-premises |
| --- | --- |
| Key Vault com soft delete/purge protection, private endpoint, RBAC | mecanismo de segredos on-premises: **DPAPI (nó único)**; **perfil HA de segredos = `BLOCKED_PENDING_EVIDENCE`** até mecanismo multi-nó concreto ser escolhido e certificado; Certificate Store; ACLs — ADR-0003 |
| SAS com content type/expiry/tags; sem valor em tag | custódia + validação host/HTTPS/container/expiry; tags de wave; nunca em log/analytics/telemetria — ADR-0006 |
| HMAC por tenant, versionadas; rotação preserva fingerprints | inalterado (app-level); `keyVersion` persistida |
| assinatura com chave não exportável; HSM se justificar | chave não exportável em **TPM/HSM local** quando justificado |
| renovação de certificado + alarmes 30/14/7 | inalterado |
| redaction central de telemetria | inalterado (§32.1) com canary tests |

> **HA e segredos:** conforme ADR-0003, **DPAPI por máquina serve apenas ao
> perfil de nó único**; o **perfil HA de segredos fica `BLOCKED_PENDING_EVIDENCE`**
> até um **mecanismo multi-nó concreto** (key ring protegido por certificado,
> store corporativo homologado, HSM ou solução de secrets management do
> cliente) ser escolhido e certificado — registrado como risco residual
> (seção 6). Uma interface vaga "multi-nó" não conta como solução pronta.

## 4. Storage e hardening (§33/§34)

- Ciclo de vida por container (§33: `landing`, `work`, `parts`,
  `quarantine`, `evidence`, `reports`) realizado em **NTFS/NAS/SMB** do
  cliente, com ACL exclusiva, encryption at rest (BitLocker/volume), WORM
  para `evidence`.
- Hardening dos workers Windows (§34) **já é on-premises**: gMSA/virtual
  service account, WDAC/App Control, JEA, Credential Guard, BitLocker, Secure
  Boot/vTPM, RDP desabilitado (break-glass por JIT/PIM), SMBv1/TLS legado
  desabilitados, saída apenas para destinos necessários. Após cada uso do SAS,
  **higiene padrão** (destruir cópia local, encerrar processo, limpar
  temporários, verificar logs/dumps, health check); **reimage apenas após
  incidente, comprometimento ou suspeita de exposição** — não como rotina.
- Malware/conteúdo hostil (§35): sem execução de macros/scripts/preview;
  extração de anexo só em diretório `noexec`/ACL com scan; HTML não
  renderizado sem sanitização.

## 5. Avaliação de dados e privacidade (LGPD)

- **Categorias de dados pessoais:** conteúdo de e-mail (assunto, corpo,
  anexos), endereços SMTP/UPN, metadados de mailbox. **Alta sensibilidade.**
- **Minimização:** o plano de controle persiste **apenas metadados**
  (IDs, hashes, contagens, estados); **conteúdo nunca entra no SQL Server** —
  permanece nos artefatos protegidos (ADR-0003). Logs proíbem assunto,
  corpo, nome de anexo, SAS, token e path real (§39.1), com **UPN/SMTP
  substituídos por HMAC de tenant** na telemetria (§32.1).
- **Retenção:** por container (§33); staging Microsoft com retenção
  controlada pela Microsoft (ADR-0006/§25.10); `evidence` WORM 7–10 anos ou
  política.
- **Transferência internacional:** o destino Microsoft 365 é **externo**; a
  migração transfere conteúdo do cliente ao tenant M365 do próprio cliente
  por HTTPS 443 de saída. Base legal, DPA e localização do tenant são
  **decisão do controlador (cliente) + DPO** — fora do escopo técnico deste
  ADR; registrado como pendência de avaliação (seção 6).
- **Titular e direitos:** o produto não é o controlador; opera a migração
  sob instrução do cliente. Trilha de auditoria e cadeia de custódia
  suportam atendimento a requisições, mas a resposta é do controlador.

## 6. Riscos residuais e pendências para Segurança/DPO

1. **Mecanismo de segredos multi-nó (HA)** — perfil HA de segredos fica
   `BLOCKED_PENDING_EVIDENCE` até um mecanismo concreto (key ring por
   certificado / store corporativo / HSM / secrets management do cliente) ser
   escolhido e certificado. DPAPI de nó único não cobre HA.
2. **Base legal, DPA e localização do tenant M365** — avaliação do DPO por
   engajamento (transferência internacional / soberania de dados).
3. **Threat model por engajamento** — este é o modelo de produto; cada
   implantação valida a matriz de fluxos/portas (ADR-0003) e a segmentação
   de rede do cliente.
4. **Testes de autorização cross-tenant** — não há código antes do
   scaffolding; portanto os testes de arquitetura, autorização e isolamento
   cross-tenant devem **existir no primeiro PR de scaffolding** e ser
   obrigatórios desde o primeiro módulo que persista ou consulte dados
   escopados por tenant (§37), com canaries de RLS.

## 7. Conclusão e assinatura (a preencher na revisão)

- **Parecer de Segurança (assinatura/data):** _(pendente)_
- **Parecer do DPO/Privacidade (assinatura/data):** _(pendente)_
- **Ressalvas/condições:** _(pendente)_

A **aceitação formal** do ADR-0008 é ato do Decision Owner (Vinicius
Miranda) e ocorre **somente após** a revisão de Segurança/DPO estar
registrada — conforme a [matriz de fechamento](../gate-closure-matrix.md).
