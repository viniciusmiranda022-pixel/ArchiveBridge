# Slice 4A — Control Plane API e Portal Operacional

> **Status:** primeira fatia com interface funcional. **Somente leitura.** Sob autenticação e RBAC.
> Não executa nem simula nenhuma capacidade do Slice 4B (exportação do Enterprise Vault).

## Objetivo

Entregar o **plano de controle** do ArchiveBridge como uma aplicação **on-premises** navegável, consumindo
o que já foi construído nos Slices 1–3 (jobs duráveis, projetos/ondas/mapping, descoberta de capacidades do
Enterprise Vault). O operador autentica-se, enxerga o estado da plataforma dentro do **seu tenant/projeto**
e audita o que aconteceu — sem iniciar exportação ou ingestão.

## Princípio: a interface não simula o que ainda não existe

Onde uma capacidade ainda não foi implementada, a tela declara **explicitamente** que está indisponível e
por quê — nunca um botão que aparenta funcionar. Exemplos nesta fatia:

- **Exportação** (`/Export`): "Status: indisponível — Slice 4B ainda não implementado".
- **Disparo de descoberta**: o portal mostra os resultados já registrados; enfileirar nova descoberta é
  ação de escrita e entra em rodada seguinte.
- **Gestão de usuários**: permanece indisponível pela interface; provisionamento inicial é controlado.
- **Download de evidência**: implementado como leitura autenticada e verificada, sem reserializar os bytes.

## Arquitetura

```text
Navegador
    ↓ HTTPS (TLS terminado no IIS/Windows Service)
ArchiveBridge.ControlPlane  (ASP.NET Core, Microsoft.NET.Sdk.Web)
    ├── Portal Web (Razor Pages, CSS autocontido — sem CDN, sem script externo)
    ├── Autenticação por cookie + RBAC (fallback fail-closed: tudo exige login)
    ├── Read-model (IControlPlaneQueries) — tenant RLS + filtro explícito por projeto
    ├── IEvDiscoveryEvidenceStore — valida bundle imutável on-premises antes do download
    ├── IPortalOperationalAudit — trilha append-only sob tenant/projeto
    └── Identidade do portal (IPortalUserStore / IPortalSignInAudit)
            ↓
        SQL Server on-premises + filesystem/SMB de evidências do Slice 3
```

Mantém a arquitetura aprovada: **on-premises**; **IIS ou Windows Service**; **sem** Azure App Service,
banco em nuvem, SaaS da ArchiveBridge ou comunicação externa além das integrações explicitamente
configuradas. A camada `Infrastructure` permanece livre de dependências de ASP.NET; a composição vive no
host.

## Autenticação e RBAC

- **Contas locais + cookie**, com o hash de senha derivado por **PBKDF2-HMAC-SHA256** (sal por usuário,
  210k iterações, verificação em tempo constante). Nenhuma senha em claro é armazenada, registrada em log
  ou versionada.
- A porta `IPasswordHasher` e o modelo de identidade estão isolados para permitir, em rodada futura, um
  provedor externo (ex.: **Windows Authentication/AD**) sem reescrever as telas.
- **Papéis (RBAC):** `Viewer`, `Operator`, `Approver`, `Auditor`, `Administrator`. As leituras de evidência
  são permitidas a qualquer papel autenticado dentro do próprio escopo; a área de **Administração** exige
  `Administrator`, e `/Audit` exige `Auditor` ou `Administrator`.
- **Fail-closed:** a política padrão exige usuário autenticado; apenas login, acesso-negado e health são
  anônimos. Toda tentativa de login (sucesso e falha) é auditada — sem segredo.
- **Escopo efetivo:** tenant, projeto e identidade persistida do usuário vêm das claims emitidas no login;
  o cliente nunca informa tenant/projeto como se fosse autorização.

## Isolamento por tenant (RLS) **e** por projeto (filtro explícito)

O read-model abre conexões da identidade da aplicação com `SESSION_CONTEXT('tenant_id')` = tenant do
usuário (mesma `TenantConnectionFactory` da produção) **e** filtra explicitamente por `project_id = @project`
em todas as consultas de negócio. O isolamento é, portanto, **por tenant (RLS) e por projeto (filtro)**.
Um usuário vinculado a um projeto nunca enxerga outro projeto do mesmo tenant; pedidos de recurso de outro
projeto retornam a mesma ausência usada para inexistente (anti-IDOR).

A auditoria de login não está sob RLS porque participa do estabelecimento da identidade; sua leitura é
filtrada por tenant. Já a **auditoria operacional** ocorre depois da autenticação e fica sob RLS por tenant,
FK coerente `(tenant_id, project_id)` e filtro explícito por projeto.

## Download verificado de `evidence.json`

O download reutiliza integralmente o contrato e o store imutável do Slice 3. O Portal **não lê arquivo por
caminho fornecido pelo cliente**. O fluxo é:

1. ambiente + versão são resolvidos no SQL dentro do tenant/projeto autenticado;
2. inexistente/cross-tenant/cross-project retorna `404` sem revelar a existência do recurso;
3. o caminho lógico esperado é reconstruído deterministicamente pelo `EvDiscoveryEvidenceDescriptor`;
4. `FileSystemEvDiscoveryEvidenceStore.GetAsync` valida containment/path traversal, presença dos **três e
   somente três** arquivos (`evidence.json`, `evidence.sha256`, `manifest.json`), sidecar SHA-256, manifesto,
   tenant/projeto/ambiente/versão, caminho lógico e tamanho;
5. o Portal reconcilia novamente `content_sha256`, tamanho e caminho lógico contra a âncora SQL;
6. qualquer divergência retorna falha genérica e registra auditoria operacional;
7. a auditoria de sucesso é persistida **antes** da entrega; falha ao auditar impede o download;
8. os bytes validados são entregues diretamente como `application/json`/attachment, sem parse/serialização.

`EvidenceHash` no SQL é o **hash semântico** da evidência. A integridade byte-a-byte do arquivo é ancorada
por `content_sha256`, que é o valor comparado ao SHA-256 calculado pelo store.

## Telas

| Rota | Conteúdo |
| --- | --- |
| `/` | Painel inicial de projetos, ondas, jobs e resultados EV. |
| `/Projects` | Projeto do escopo autenticado. |
| `/Waves` | Ondas do projeto. |
| `/Jobs` + `/Jobs/Details` | Fila durável e transições de estado no projeto. |
| `/EnterpriseVault` | Descoberta de capacidades por ambiente. |
| `/Evidence` | Metadados + link para download verificado de `evidence.json`. |
| `/Evidence/Download` | Entrega autenticada, anti-IDOR e verificada do artefato imutável. |
| `/Audit` | Autenticação do tenant + eventos operacionais do tenant/projeto (Auditor/Administrator). |
| `/Export` | **Indisponível — Slice 4B**. |
| `/Admin` | Restrito a `Administrator`. |

## Segurança da superfície web

- **CSP restrita** (`default-src 'self'`), `X-Content-Type-Options`, `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`; sem CDN/script externo.
- **Antiforgery** nos POST baseados em cookie.
- Cookie `HttpOnly`, `SameSite=Lax`, expiração deslizante de 8 h; `SecurePolicy=Always` fora de
  desenvolvimento.
- **HTTPS obrigatório fora de desenvolvimento:** `UseHsts()` + `UseHttpsRedirection()`.
- **Equalização de timing no login:** usuário inexistente ainda executa derivação PBKDF2 dummy.
- Download de evidência: `Cache-Control: no-store`, filename fixo, nenhuma entrada do cliente vira caminho
  físico, validação fail-closed do bundle + fingerprint SQL, logs sem caminho físico/segredo.

> Ainda pendentes de hardening: **rate limiting** de login, gates de **SAST/DAST**, **coverage/mutation** e
> revisão independente.

## Nota sobre o dashboard

O painel entrega o **read-model inicial**. O dashboard operacional completo da especificação (todos os
estados, tentativas/retries agregados, aprovações pendentes e eventos recentes) permanece parcial.

## Persistência

- **Migration `0014_slice4a_portal_identity.sql`**: identidade do portal e auditoria de autenticação.
- **Migration `0015_slice4a_operational_audit.sql`**: trilha operacional append-only sob RLS, FK de escopo,
  índice tenant/projeto/tempo e apenas `SELECT/INSERT` para a identidade da aplicação.
- `ev_discovery_runs.content_sha256`, `evidence_path` e `evidence_size_bytes` continuam sendo as âncoras SQL
  do Slice 3; o conteúdo permanece no storage imutável, não no banco.

## Provisionamento (on-premises)

Um administrador inicial pode ser criado no primeiro start **se** o portal estiver vazio **e** uma senha de
bootstrap for injetada pelo ambiente. A raiz do storage de evidência é configurada por
`ControlPlane:EvidenceRoot`; caminho relativo é resolvido sob o `ContentRoot`, e implantação pode apontar
para volume/SMB on-premises com ACL apropriada.

## Stage 4 — Solicitação autorizada de descoberta EV (primeira ação de escrita)

A primeira ação de **escrita** do Portal: um usuário autenticado e autorizado SOLICITA uma descoberta EV
READ-ONLY. O HTTP **não executa descoberta** — apenas resolve o escopo autenticado e chama
`RequestEvCapabilityDiscoveryUseCase`, que enfileira um Job durável idempotente. O Worker EV (fora do
processo web) processa depois. O caminho:

```
POST /EnterpriseVault?handler=RequestDiscovery
  → fallback policy (autenticado) + antiforgery (Razor Pages)
  → UserId (PortalClaims.UserId) válido? senão 403
  → TenantScope = IPortalScopeAccessor.Resolve(User)   (nunca do formulário)
  → IAuthorizationService.AuthorizeAsync("EvDiscoveryOperators")  → 403 + audit forbidden se negado (PRECEDE o gate)
  → feature gate do deployment habilitado? senão 503 (audit feature-disabled)
  → EnvironmentId/IdempotencyKey GUID não vazios? senão 400
  → RequestEvCapabilityDiscoveryUseCase.ExecuteAsync(scope, env, RequestedBy=User.Identity.Name, key, correlation-server)
        · ambiente resolvido no SQL sob o escopo (fora do escopo ⇒ 404 anti-IDOR)
        · site/directory, versão/hash de configuração e versão da política resolvidos server-side
        · enqueue durável idempotente (índice único filtrado como backstop)
  → audit accepted|idempotent-replay (ANTES do redirect; falha ⇒ 500 fail-closed)
  → PRG 302 → /Jobs/Details?jobId=…
```

Entradas aceitas do navegador: **somente** `EnvironmentId` e `IdempotencyKey`. Tenant/projeto vêm do
principal; `RequestedBy` é `User.Identity.Name`; o `CorrelationId` é criado no servidor. Campos extras do
formulário (TenantId, ProjectId, SiteName, DirectoryServer, ConfigurationVersion/Hash, DiscoveryPolicyVersion,
RequestedBy, CorrelationId, JobId, Role) **não são vinculados**. A policy `EvDiscoveryOperators`
(Operator/Administrator) protege a **ação POST** via `IAuthorizationService` — não a página, que continua
legível pelos papéis de leitura. O gate é uma opção **local** do Control Plane
(`EnterpriseVaultDiscoveryPortalOptions`, seção `EnterpriseVaultDiscovery`, default `Enabled=false`), sem
dependência de projeto `ControlPlane → Workers.Ev`.

### Contrato HTTP

`302 → /Jobs/Details` (accepted/replay) · `400` (input inválido / antiforgery inválido) · `403` (papel não
autorizado / principal inválido) · `404` (ambiente inexistente/fora do escopo) · `409` (conflito de
idempotência) · `503` (gate desabilitado) · `500` (falha inesperada / auditoria obrigatória falhou).

### Threat-model delta (write-path)

| Ameaça | Controle |
| --- | --- |
| CSRF | antiforgery Razor Pages (token+cookie); POST sem token ⇒ 400 |
| IDOR (ambiente de outro projeto/tenant) | catálogo resolve só no escopo; `404` indistinguível; audit `not-found-or-not-authorized` |
| Escalonamento de papel | `EvDiscoveryOperators` server-side; POST manual de Viewer/Auditor/Approver ⇒ 403 + audit `forbidden` |
| Vazamento do estado do gate | RBAC **precede** o feature gate: um principal sem mandato recebe `403` com `Enabled=true` **ou** `false` (não infere o gate pela resposta); só um usuário autorizado vê `503`/`feature-disabled` |
| Substituição de tenant/projeto | escopo derivado só do principal (`IPortalScopeAccessor`) |
| Substituição do contexto do comando | site/directory/versão/hash/política resolvidos server-side; campos do formulário ignorados |
| Double-submit / retry | idempotency key por formulário (estável no GET) ⇒ replay, 1 Job |
| Conflito de idempotência | mesma chave + comando diferente ⇒ `409` + audit `idempotency-conflict`; nenhum Job novo |
| Perda de auditoria | auditoria de resultado ANTES do redirect; falha ⇒ `500` fail-closed; Job durável permanece; retry com a mesma chave devolve o mesmo Job |
| Execução de discovery inline | proibida no processo web; só `RequestEvCapabilityDiscoveryUseCase`; teste arquitetural garante ausência de processor/host/PowerShell |
| Bypass do feature gate | gate `Enabled=false` ⇒ nenhum Job, `503`, audit `feature-disabled` (mesmo para usuário autorizado) |

**Risco residual:** o enqueue durável e a trilha operacional **não compartilham uma transação distribuída**.
Se a auditoria de sucesso falhar após o enqueue, respondemos `500` (fail-closed) sem compensação destrutiva;
o Job permanece e o retry com a mesma chave de idempotência é seguro (devolve o mesmo Job).

Eventos operacionais (`ev.discovery.request`): `accepted`, `idempotent-replay`, `idempotency-conflict`,
`not-found-or-not-authorized`, `forbidden`, `feature-disabled`, `invalid-input`. Nunca são auditados
site, directory server, hash de configuração, PowerShell, stdout/stderr, evidência, senha, token ou cookie.

## Fora do escopo desta fatia

Execução de `Export-EVArchive`; geração de PST; Purview; Microsoft Graph; AzCopy; ingestão no Microsoft
365; reconciliação final; homologação real contra Enterprise Vault (`LaboratoryValidated` permanece
`false`). Ações de escrita como aprovação, upload/validação de CSV, retry autorizado e gestão de usuários
permanecem em incrementos seguintes. **O disparo de descoberta passou a ser suportado como SOLICITAÇÃO
durável (Stage 4) — o processo web nunca executa a descoberta.**

## Testes

- SQL real: identidade, auditoria, isolamento tenant/projeto e resolução de evidência anti-IDOR.
- HTTP + filesystem real: download retorna **bytes exatamente iguais** aos publicados; bundle adulterado é
  recusado; recurso de outro projeto no mesmo tenant retorna `404`; sucesso/falha de leitura são auditados.
- O store do Slice 3 continua sendo o responsável por containment/path traversal, sidecar, manifesto,
  hash e conjunto fechado de arquivos.
