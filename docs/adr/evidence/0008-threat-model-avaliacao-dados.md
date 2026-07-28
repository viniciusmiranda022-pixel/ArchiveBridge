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
- **Retenção (PRIV-01):** a retenção de evidências, relatórios, logs e
  artefatos é **definida por política versionada por engajamento**, com
  **fundamento legal, contratual, regulatório e operacional** documentado pelo
  controlador, aplicando o **mínimo necessário**. **Não há prazo padrão
  universal** — qualquer prazo (ex.: 7–10 anos) é **perfil específico de
  engajamento, nunca default**. A política **diferencia**: PST original,
  parts, scratch, quarantine, logs, mapping CSV, manifestos, relatórios,
  evidência WORM, backups e registros de incidente. Staging Microsoft tem
  retenção controlada pela Microsoft (ADR-0006/§25.10).
- **Transferência internacional:** o destino Microsoft 365 é **externo**; a
  migração transfere conteúdo do cliente ao tenant M365 do próprio cliente
  por HTTPS 443 de saída. Base legal, DPA e localização do tenant são
  **decisão do controlador (cliente) + DPO** — fora do escopo técnico deste
  ADR; registrado como pendência de avaliação (seção 6).
- **Papéis LGPD (PRIV-03) — hipótese contratual, não verdade universal:** no
  **modelo contratual esperado**, o cliente tende a atuar como **controlador**,
  a TISCO/operador da migração como **operadora** e a Microsoft como
  **suboperadora/provedora** do serviço de destino, conforme contratos e
  instruções documentadas. **Os papéis efetivos devem ser confirmados por
  engajamento**; o software, isoladamente, **não determina** a qualificação
  jurídica. Trilha de auditoria e cadeia de custódia suportam atendimento a
  requisições, mas a resposta é do controlador. (Matriz mínima de papéis na
  seção 7, condição PRIV-03.)

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

## 7. Condições do parecer técnico de Segurança e Privacidade

> **Resultado da revisão técnica preliminar: APROVAÇÃO TÉCNICA CONDICIONAL.**
> O desenho on-premises do ADR-0008 é tecnicamente adequado, sujeito às
> condições de segurança, privacidade, implementação e produção documentadas
> no parecer técnico. Ver o parecer completo em
> [`0008-parecer-tecnico-seguranca-privacidade.md`](0008-parecer-tecnico-seguranca-privacidade.md).
> **Não** constitui assinatura institucional de Segurança/DPO (pendente) nem
> parecer jurídico. A aceitação do ADR **não** equivale a autorização de produção.

As condições abaixo são incorporadas ao ADR/evidência como **correções** e
como **gates verificáveis** do primeiro scaffolding e da promoção a produção.

### SEC-01 — Contexto de tenant no SQL (contrato de implementação)

- `tenant_id` **derivado da identidade/autorização do servidor**, **nunca** de valor enviado pelo cliente;
- configurar `SESSION_CONTEXT` por **unidade de trabalho**; **limpar/sobrescrever** o contexto **antes de devolver a conexão ao pool**;
- **impedir alteração não autorizada** do contexto por queries comuns;
- tenant/projeto em **chaves, unique constraints, foreign keys e índices** aplicáveis;
- **testes de reutilização de conexão entre tenants**; testar `SELECT/INSERT/UPDATE/DELETE`, procedures e operações em lote;
- **falhar fechado** quando não houver tenant válido. **RLS continua defesa em profundidade — não substitui a autorização na Application.**

### IAM-01 / IAM-02 — Identidades Windows e por operação

- **gMSA como baseline** quando o workload acessar **SQL, SMB/NAS, EV ou outro recurso de rede autenticado**; **virtual service account** apenas em nó único quando o acesso de rede puder usar a identidade da máquina sem ampliar privilégio;
- **uma identidade por workload**; **nunca** reutilizar a mesma gMSA entre workloads por conveniência;
- **logon interativo proibido**; **grupos administrativos amplos proibidos**;
- restringir **`PrincipalsAllowedToRetrieveManagedPassword`** somente aos hosts autorizados;
- identidades externas **separadas por operação** (Precheck M365 / reconciliação / Graph condicional / Purview Operator / Purview Approver / Upload Worker); precheck/reconciliação **sem** permissão de escrita quando só leitura é necessária.

### IAM-03 — Exchange Online RBAC for Applications (grants aditivos)

- **inventariar todas as permissões e consentimentos** da aplicação;
- **impedir** que o mesmo service principal tenha **permissões amplas no Entra ID** que contornem o management scope do Exchange Online;
- **teste positivo** de acesso às mailboxes da onda e **teste negativo** às mailboxes fora da onda (o gate **falha** se acessar fora do escopo);
- registrar **`appId`, service principal, role assignment, management scope e hash da configuração**.

### SEC-02 — DPAPI (nó único)

- DPAPI é **baseline somente para perfil de nó único**, com **escopo da identidade dedicada do serviço**;
- **não** usar proteção de máquina (`CRYPTPROTECT_LOCAL_MACHINE`) como controle único para segredos de tenant;
- **procedimento de backup e recuperação** das chaves; **testar restauração e troca controlada do host**;
- **nunca** persistir segredo em texto claro; zerar buffers quando possível; registrar **owner, finalidade, versão e expiração**; **separar segredos por tenant/operação** quando aplicável.

### SEC-03 — HA de segredos (mantido bloqueado)

- **HA de segredos = `BLOCKED_PENDING_EVIDENCE`**. A habilitação exige **ADR/evidência específica** com: solução concreta (ex.: DPAPI-NG, key ring por certificado, HSM, secrets manager corporativo), threat model, rotação, backup, recuperação, revogação, auditoria, disponibilidade, segregação por tenant e **teste de perda de nó**. **Não** escolher uma solução definitiva agora apenas para fechar o ADR.

### SEC-04 — SAS do Purview (exposição em processo)

- **não registrar command line**; desabilitar **transcript/history**; **restringir visibilidade de processos**; **controlar dumps** (desabilitados salvo procedimento de incidente controlado); **sanitizar stdout/stderr**; aplicar **timeout**; **destruir cópias locais** do SAS; **leak test** em logs, eventos, dumps e pacote de evidência. Dependência mantida do **ADR-0006**.

### NET-01 — Matriz de fluxos verificável (por implantação)

- mantidos: **nenhuma entrada da internet**; **egress externo somente HTTPS 443** aos endpoints Microsoft autorizados; **fluxos internos registrados separadamente**;
- para **cada implantação**, registrar por regra: **origem, destino, protocolo, porta, direção, FQDN/DNS, identidade, finalidade, owner, ambiente, justificativa, evidência de teste, e revisão/expiração da regra**. Não liberar wildcard amplo quando houver alternativa suportada.

### PRIV-01 — Retenção

Incorporada na seção 5: **sem prazo padrão universal**; política por engajamento, mínimo necessário, com fundamento legal/contratual/regulatório/operacional, diferenciando PST original, parts, scratch, quarantine, logs, mapping CSV, manifestos, relatórios, evidência WORM, backups e registros de incidente.

### PRIV-03 — Matriz mínima de papéis LGPD (a confirmar por engajamento)

| Item | Registro (por engajamento) |
| --- | --- |
| Controlador | _(a confirmar — tende ao cliente)_ |
| Operador | _(a confirmar — tende à TISCO/operador da migração)_ |
| Suboperadores/subprocessadores | _(a confirmar — inclui Microsoft como provedora do destino)_ |
| Finalidade | _(a documentar)_ |
| Instruções documentadas | _(do controlador)_ |
| Categorias de dados | conteúdo de e-mail, SMTP/UPN, metadados de mailbox |
| Titulares | _(a documentar)_ |
| Retenção | _(por política — PRIV-01)_ |
| Localização | _(tenant e workloads — PRIV-04)_ |
| Atendimento de direitos | _(canal e responsabilidade do controlador)_ |
| Resposta a incidentes | _(canal ao controlador — IR-01)_ |
| Retorno/eliminação ao encerrar | _(procedimento a documentar)_ |

### PRIV-04 — Transferência internacional e DPA (checklist por projeto)

Antes da execução: localização do tenant e workloads; **DPA vigente da Microsoft**; **mecanismo aplicável da Resolução CD/ANPD nº 19/2024**; subprocessadores; base legal; instrução documentada do controlador; risco residual de soberania/jurisdição; **aprovação do responsável de privacidade do cliente**. **Não** se conclui juridicamente que uma transferência específica está regularizada.

### IR-01 — Resposta a incidentes (requisito operacional)

Detectar e **preservar evidências**; classificar impacto; identificar **dados e titulares afetados**; **escalar imediatamente ao controlador**; fornecer informações para decisão regulatória; suportar comunicação em fases; manter registro do incidente. **Nunca** comunicar diretamente à ANPD ou aos titulares **em nome do controlador sem mandato contratual**.

### Threat model e testes do primeiro scaffolding (evolução por slice)

Além dos itens da seção 6, o primeiro scaffolding adiciona testes para: **connection pooling/session context; tenant spoofing; confused deputy; acesso cross-tenant por IDs previsíveis; grants amplos no Entra ID; gMSA em host não autorizado; restore de segredo em host incorreto; SAS em process list/log/dump; path traversal e reparse point; PST hostil e resource exhaustion; replay de eventos; operação externa ambígua; overwrite em WORM; canary de redaction; assinatura inválida de evidência.**

### Condições obrigatórias antes de produção (resumo)

Pen-test de autorização; testes cross-tenant completos; restore DPAPI; validação de retrieval de gMSA por host; consent inventory M365; teste negativo fora do EXO management scope; SAS leak test; matriz de fluxos aprovada; testes de filesystem/reparse point; WORM restore/verification; backup/restore; incident tabletop; **DPA e transferência internacional validados**; **política de retenção aprovada**; mecanismo HA de segredos certificado (se HA for habilitado); **revisão de risco residual assinada**.

## 8. Conclusão e assinatura (a preencher na revisão)

- **Resultado da revisão técnica preliminar:** **APROVAÇÃO TÉCNICA CONDICIONAL** (seção 7 e parecer anexo)
- **Parecer de Segurança (assinatura institucional/data):** _(pendente)_
- **Parecer do DPO/Privacidade (assinatura institucional/data):** _(pendente)_
- **Ressalvas/condições:** as condições SEC-01..04, IAM-01..03, NET-01, PRIV-01..04 e IR-01 (seção 7)

A **aceitação formal** do ADR-0008 é ato do Decision Owner (Vinicius
Miranda) e ocorre **somente após** a revisão de Segurança/DPO estar
registrada — conforme a [matriz de fechamento](../gate-closure-matrix.md).
