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

## Passo 5 — Paginação keyset + filtros (histórico de evidências e auditoria)

Superfícies **somente leitura** com paginação **keyset/seek** (estável sob inserções concorrentes, custo
independente do número da página, sem `OFFSET` e sem `COUNT(*)`), e filtros bounded/parametrizados.

- **Histórico de evidências** (`/Evidence`): deixa de mostrar só a última descoberta por ambiente e passa a
  ser um histórico navegável de execuções **maduras** (`status NOT IN (Pending, Discovering)`), inclusive
  `Superseded`. Ordem `completed_at_utc DESC, environment_id DESC, discovery_version DESC`. Filtros: prefixo
  de site, `EnvironmentId`, resultado (`Ready`/`Blocked`/`Unsupported`/`Failed`), intervalo UTC, quantidade.
  O **download verificado** (`/Evidence/Download`) permanece inalterado.
- **Auditoria** (`/Audit`, restrita a `Auditor`/`Administrator`): duas listas **independentes** —
  operacional (tenant + projeto, sob RLS) e autenticação (tenant-wide, `tenant_id = @tenant` explícito, fora
  da RLS; eventos com tenant nulo não aparecem). Cada lista tem filtros e cursor próprios; avançar uma
  preserva o estado da outra. Ordem `occurred_at_utc DESC, event_id DESC`.

**Camadas.** Contracts define os tipos (`KeysetPage<TItem,TPosition>`, filtros, `EvidenceSeekPosition`,
`AuditSeekPosition`) sem conhecer UI. A Infrastructure executa SQL **estático** com predicados opcionais
parametrizados e o predicado de seek, recebendo uma posição já validada. O Control Plane codifica/valida o
**cursor** URL-safe.

**Cursor.** Envelope versionado (Base64Url + JSON) que carrega **apenas** as chaves de ordenação e o
**fingerprint** dos filtros. Decodificação estrita e fail-closed: cursor acima do teto de tamanho (`2048`,
checado antes de qualquer alocação), fora do alfabeto Base64Url estrito (`A–Z a–z 0–9 - _`; `=`, `+`, `/`
recusados), Base64/JSON inválido, versão desconhecida, posição malformada ou fingerprint divergente ⇒
`HTTP 400` (nunca `500`; nenhuma query com valor parcial). O cursor é **entrada não confiável** e **não é
autorização**: Base64Url **não é assinatura**, o fingerprint **não é MAC** (garante só a consistência
cursor↔filtros — um cliente deliberado pode forjar um cursor bem-formado, o que **não** concede autoridade),
e o cursor **não contém** tenant/projeto. O escopo é sempre re-resolvido do principal (RLS + `project_id =
@project`); uma posição forjada só desloca o ponto temporal da consulta já escopada, jamais troca o escopo.
Autenticidade criptográfica do cursor, se exigida, é hardening do Passo 8.

**Limites e segurança.** `pageSize` do cliente é normalizado (clamp `[1, 100]`) server-side; `TOP
(pageSize + 1)` decide `HasMore` sem contagem total. Prefixos usam `LIKE ESCAPE N'\'` com escaping literal
(curingas são dados); o parâmetro `NVARCHAR` é dimensionado pelo pior caso do escaping (`rawMax*2+1` —
SitePrefix `201`, UsernamePrefix `401`), nunca truncando o padrão no boundary. Sem ordenação dinâmica. Índices
de seek na migration **aditiva** `0017`
(`IX_evd_scope_completed`; `IX_portal_sign_in_events_tenant_time_event`; a trilha operacional já tinha
índice alinhado). Nenhuma navegação de leitura gera evento de auditoria (sem recursão). O threat-model delta
está em `docs/security/threat-model-slice-04a.md`.

## Passo 6A — Backend seguro de recebimento e validação de CSV de mapping

Núcleo **somente backend** (sem página de upload, sem POST, sem multipart, sem endpoint) que recebe um CSV
de mapping enviado pelo operador e o valida contra a fonte autorizada. **Não importa nada para o Microsoft
365** e **não retém os bytes brutos** — a custódia guarda o SHA-256 e os metadados.

**Pipeline** (`ValidateMappingCsvUploadUseCase`, sem qualquer dependência de ASP.NET):
`stream não confiável → preflight (extensão .csv, basename, declared length) → leitura BOUNDED (limit+1) →
SHA-256 dos BYTES EXATOS → UTF-8 estrito SEM BOM → validação (reutiliza MappingSchema/MappingCsvParser/
MappingCsvValidator/MappingPolicy existentes) → onda Approved/Frozen resolvida server-side → custódia durável
idempotente`. Resultado: **Valid**/**Invalid** (ou **Rejected** para encoding/BOM), com evidência persistida.

**Reúso, sem duplicação.** O esquema (10 colunas, `MaxDataRows = 500`), o parser RFC 4180 e o validator são
os do Slice 2 — o parser ganhou apenas uma **capacidade bounded** (overload) e o validator um caminho de
**problemas estruturados** (código/linha/coluna), mantendo `MappingValidationResult.Errors` como projeção.

**Custódia** (`IMappingValidationStore` / `SqlMappingValidationStore`, migration **`0018`** aditiva/append-only):
`mapping_validation_attempts` (SHA dos bytes exatos, snapshot da onda versão/hashes, esquema/política/code page,
desfecho, contagem/truncamento de problemas, metadados) + `mapping_validation_issues` (append-only). Apenas
`SELECT/INSERT` para a aplicação; **manutenção sem grant**; RLS por tenant + filtro de projeto; FK composta à
versão imutável da onda; idempotência por `(tenant, projeto, chave)` com backstop de índice único e revalidação
TOCTOU da onda na mesma transação. Detalhes e ameaças em `docs/security/threat-model-slice-04a.md`.

**Conceito distinto de `IMappingStore`** (versões GERADAS/artefatos): aqui persiste-se a **RECEPÇÃO**, não uma
versão utilizável. Nenhum byte bruto é gravado; nenhum artefato é criado; um CSV `Valid` **não** significa
importação aprovada — o único efeito é a evidência de validação.

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
