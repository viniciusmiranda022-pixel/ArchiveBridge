# Slice 4A — Control Plane API e Portal Operacional

> **Status:** primeira fatia com interface funcional. **Somente leitura.** Sob autenticação e RBAC.
> Não executa nem simula nenhuma capacidade do Slice 4B (exportação do Enterprise Vault).

## Objetivo

Entregar o **plano de controle** do ArchiveBridge como uma aplicação **on-premises** navegável, consumindo
o que já foi construído nos Slices 1–3 (jobs duráveis, projetos/ondas/mapping, descoberta de capacidades do
Enterprise Vault). O operador autentica-se, enxerga o estado da plataforma dentro do **seu tenant** (isolado
por RLS) e audita o que aconteceu — tudo **sem** disparar execução.

## Princípio: a interface não simula o que ainda não existe

Onde uma capacidade ainda não foi implementada, a tela declara **explicitamente** que está indisponível e
por quê — nunca um botão que aparenta funcionar. Exemplos nesta fatia:

- **Exportação** (`/Export`): "Status: indisponível — Slice 4B ainda não implementado".
- **Disparo de descoberta**: o portal mostra os resultados já registrados; enfileirar nova descoberta é
  ação de escrita e entra em rodada seguinte.
- **Download de evidência** e **gestão de usuários**: marcados como próxima rodada, com o dado factual
  (hashes/caminho) exibido para conferência.

## Arquitetura

```text
Navegador
    ↓ HTTPS (TLS terminado no IIS/Windows Service)
ArchiveBridge.ControlPlane  (ASP.NET Core, Microsoft.NET.Sdk.Web)
    ├── Portal Web (Razor Pages, CSS autocontido — sem CDN, sem script externo)
    ├── Autenticação por cookie + RBAC (fallback fail-closed: tudo exige login)
    ├── Read-model (IControlPlaneQueries) — leitura sob RLS por tenant
    └── Identidade do portal (IPortalUserStore / IPortalSignInAudit)
            ↓
        SQL Server on-premises (mesmo banco dos Slices 1–3)
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
- **Papéis (RBAC):** `Viewer`, `Operator`, `Approver`, `Auditor`, `Administrator` (conforme a especificação
  de design do slice). As telas de leitura são abertas a qualquer papel autenticado; a área de
  **Administração** exige `Administrator` (política + autorização de pasta). O catálogo de papéis é fechado
  por FK/CHECK no banco (fail-closed). As ações de escrita de rodadas futuras serão gated pelos papéis
  correspondentes (ex.: retry/discovery por `Operator`, aprovações por `Approver`).
- **Fail-closed:** a política padrão exige usuário autenticado; apenas login, acesso-negado e
  `/health/live` são anônimos. Toda tentativa de login (sucesso e falha) é **auditada** — sem segredo.
- **Escopo efetivo:** o tenant e o projeto do usuário autenticado (claims) são a **única** fonte do escopo
  de dados; o cliente nunca informa tenant como se fosse autorização.

## Isolamento por tenant (RLS)

O read-model abre conexões da identidade da aplicação com `SESSION_CONTEXT('tenant_id')` = tenant do
usuário (mesma `TenantConnectionFactory` da produção). Um tenant **nunca** enxerga projetos, ondas, jobs ou
ambientes de outro — comprovado por teste de integração contra SQL real.

## Telas (somente leitura nesta fatia)

| Rota | Conteúdo |
| --- | --- |
| `/` | Painel: contagens de projetos, ondas, jobs (pendentes/execução/falha) e EV (Ready/Blocked/Unsupported). |
| `/Projects` | Projetos do tenant (governança; sem segredo/conteúdo). |
| `/Waves` | Ondas: capacidade planejada, aprovação/congelamento. |
| `/Jobs` + `/Jobs/Details` | Fila durável: estado, tentativas, dono, erro; trilha de transições por job. |
| `/EnterpriseVault` | Descoberta de capacidades: `Ready`/`Blocked`/`Unsupported` por ambiente, com evidência. |
| `/Evidence` | Metadados do artefato imutável de evidência (hashes, caminho, tamanho). |
| `/Audit` | Tentativas de autenticação no portal. |
| `/Export` | **Indisponível — Slice 4B** (declarado honestamente). |
| `/Admin` | Restrito a `Administrator`: modelo de papéis e contagem de usuários. |

## Segurança da superfície web

- **CSP restrita** (`default-src 'self'`), `X-Content-Type-Options`, `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`. Página **autocontida** (CSS próprio; sem CDN, fonte ou script externo).
- **Antiforgery** em todos os POST (login/logout).
- Cookie `HttpOnly`, `SameSite=Lax`, expiração deslizante de 8 h.

## Persistência

- **Migration `0014_slice4a_portal_identity.sql`** (aditiva; não altera 0001–0013): `portal_users`,
  `portal_roles` (catálogo semeado), `portal_user_roles`, `portal_sign_in_events`, com GRANTs à identidade
  da aplicação. As tabelas de identidade **não** estão sob a RLS por tenant — são o mecanismo que
  estabelece o tenant do usuário; o isolamento é por privilégio e constraint.

## Provisionamento (on-premises)

Um administrador inicial pode ser criado no primeiro start **se** o portal estiver vazio **e** uma senha de
bootstrap for injetada pelo ambiente (jamais versionada). Senha vazia ⇒ bootstrap desabilitado
(fail-closed). Migrations são um passo de implantação/CI explícito (identidade com DDL, nunca a da
aplicação); o start pode aplicá-las opcionalmente via `ControlPlane:RunMigrationsAtStartup`.

## Fora do escopo desta fatia

Execução de `Export-EVArchive`; geração de PST; Purview; Microsoft Graph; AzCopy; ingestão no Microsoft
365; reconciliação final; homologação real contra Enterprise Vault. Ações de **escrita** (aprovações,
upload/validação de CSV, disparo de descoberta, gestão de usuários) entram em rodadas seguintes.

## Testes

- **Unidade/SQL real:** hash de senha (round-trip, senha errada, hash adulterado, algoritmo desconhecido);
  store de usuários (criação atômica, papéis, login duplicado e papel desconhecido recusados fail-closed);
  auditoria de login; read-model com **isolamento por tenant** e mapeamento de descoberta EV.
- **HTTP real (WebApplicationFactory):** host sobe (`/health/live`); página protegida redireciona ao login
  (fail-closed); login por formulário de ponta a ponta; **RBAC** nega Administração a um Auditor e permite
  a um Administrator; senha errada não autentica.
