# ArchiveBridge — Interface do Control Plane (Client Demo)

Interface **demonstrável** do ArchiveBridge — *Enterprise Archive Migration Platform*. Esta frente é
**estritamente visual/UX**: reorganiza e reveste o Portal Operacional (ASP.NET Core Razor Pages, dentro de
`ArchiveBridge.ControlPlane`) para parecer uma plataforma enterprise de migração e governança, **sem alterar
nenhuma regra de negócio nem enfraquecer qualquer proteção existente**.

> Não introduz React/Angular/Vue/SPA, não adiciona pacotes de fornecedor, não cria endpoint de upload, não
> executa `Export-EVArchive` nem importação para o Microsoft 365. `LaboratoryValidated` permanece `false`.

## Stack e restrições

- **ASP.NET Core Razor Pages** (mantida). HTML semântico, **CSS próprio**, componentes/partials, **JS mínimo**.
- **Sem CDN**: toda a folha de estilo (`wwwroot/css/site.css`) e o script (`wwwroot/js/site.js`) são locais;
  os ícones são **SVG inline** (`Presentation/Icons.cs`). Compatível com a **CSP restrita** já existente
  (`style-src 'self'; script-src 'self'`) — **nenhum** `style="…"` inline, `<style>` ou `on*` handler.
- Tipografia: **system stack** (Segoe UI à frente, ambiente Microsoft/on-prem), sem Google Fonts.

## Identidade visual e paleta

Linguagem enterprise, sóbria: **sidebar azul-marinho**, **workspace claro**, azul corporativo primário.
Cores centralizadas em CSS custom properties (`--ab-*`) — nenhum hex espalhado pelas páginas:

| Token | Valor | Uso |
|---|---|---|
| `--ab-primary` / `--ab-primary-hover` | `#1f5fbf` / `#184e9c` | Ação primária, item ativo |
| `--ab-sidebar` / `--ab-sidebar-2` | `#0d1b2e` / `#16283f` | Navegação |
| `--ab-background` / `--ab-surface` | `#f3f5f8` / `#ffffff` | Workspace / cartões |
| `--ab-border` / `--ab-border-strong` | `#e3e7ee` / `#cdd4df` | Bordas |
| `--ab-text` / `--ab-text-muted` / `--ab-text-faint` | `#1e2735` / `#5c6675` / `#8a94a3` | Texto |
| `--ab-success` / `--ab-warning` / `--ab-danger` / `--ab-info` / `--ab-neutral` | verde / âmbar / vermelho / azul claro / cinza | Estados semânticos |

## Layout

Shell de produto: **top bar** (breadcrumb + escopo tenant/projeto + menu de usuário) · **sidebar fixa** agrupada
· **workspace** rolável · **rodapé** com versão *Preview · 0.x* e a nota honesta de capacidades. Responsivo
para desktop enterprise (1920/1440/1366), utilizável em 1024px, sidebar colapsável em telas estreitas.

Menu (aparece apenas o que existe ou o que é claramente rotulado como futuro):

```
Dashboard
OPERAÇÃO       → Projetos · Ondas de Migração · Enterprise Vault · Mapping
GOVERNANÇA     → Evidências · Auditoria · Jobs
ADMINISTRAÇÃO  → Configurações   (apenas Administrator)
```

## Componentes reutilizáveis (`Pages/Shared`)

`_PageHeader` · `_StatusBadge` (semântica centralizada em `Presentation/StatusBadge.cs`) · `_MetricCard`
(via classe `.metric`) · `_EmptyState` · `_Alert` (`.alert`) · `_Pipeline` · `_Timeline` · `_HashDisplay`
(hash abreviado + copiar) · `_SectionCard` (`.card`). Estados **vazios**, de **carregamento** (`data-busy`) e
de **erro** (`/Error`, 400/403/404/409/500/503) são consistentes e profissionais.

## Modo de Demonstração (Presentation Mode)

Faceta **exclusiva de UI** para demonstrar a interface sem dados reais suficientes. Configuração
(`PresentationMode:Enabled`, **default `false`**):

- **Fail-closed no startup**: habilitá-lo fora de `Development`/`Staging` **aborta o processo**
  (`PresentationModeOptions.EnsureAllowedOrThrow`) — Produção nunca sobe com dados simulados.
- **Zero escrita de negócio**: o provedor (`IPresentationDataProvider` → `SyntheticPresentationDataProvider`)
  é **em memória e somente leitura**; não cria linha em SQL, não enfileira Job, não chama Worker/PowerShell,
  não gera evidência nem tentativa de validação. A única ação de escrita do portal (solicitar descoberta EV) é
  **recusada** (`403`) antes de tocar qualquer store quando o modo está ativo.
- **Banner âmbar fixo** em todas as telas: *"Modo demonstração — os dados exibidos nesta sessão são
  simulados"*.
- **Nunca mistura** real + sintético: com o modo ativo as telas leem **somente** o dataset sintético; inativo,
  **somente** os provedores reais. O provedor vive só no `ControlPlane` (verificado por teste de arquitetura).

### Dataset sintético (100% fictício)

Tenant **Contoso Demo** · projeto **Migração Enterprise Vault → Microsoft 365** · ambientes **EV-SITE-01**
(Ready) / **EV-SITE-02** (Blocked) · ondas **Executivos/Financeiro/Operações/…** · usuários `operator.demo`,
`auditor.demo`. Nenhum cliente, e-mail, PST ou tenant real.

## Funcionalidades reais × planejadas

- **Reais nesta versão**: autenticação/RBAC, dashboard, projetos, ondas, **descoberta Enterprise Vault
  (somente leitura)**, evidências com **download verificado**, auditoria (operacional + autenticação), jobs.
- **Planejadas / não disponíveis** (rotuladas como tal, nunca executáveis): exportação de arquivos, **staging**,
  **importação Microsoft 365**, reconciliação. A validação de CSV via Portal aparece como **prévia** (botão
  desabilitado) — o backend seguro entra em uma etapa posterior.

## Segurança preservada

Nada foi removido ou relaxado: `[Authorize]`/fallback policy, RBAC (Viewer/Operator/Approver/Auditor/
Administrator), antiforgery, escopo tenant/projeto, RLS, idempotência, auditoria, validação de evidência,
fail-closed, HTTPS/HSTS/cookie Secure em produção. A CSP permanece restrita; a única mudança de encoder
(`WebEncoderOptions` → `UnicodeRanges.All`) apenas renderiza acentos como UTF-8 literal — os caracteres
significativos para HTML continuam sempre codificados.

## Como rodar a demonstração (local)

```bash
# SQL Server de teste em execução; migrations aplicadas no start.
ASPNETCORE_ENVIRONMENT=Development \
PresentationMode__Enabled=true \
ControlPlane__RunMigrationsAtStartup=true \
ControlPlane__BootstrapAdmin__Password='***' \
ControlPlane__BootstrapAdmin__TenantId='<guid>' ControlPlane__BootstrapAdmin__ProjectId='<guid>' \
ConnectionStrings__Application='<conn>' ConnectionStrings__Maintenance='<conn>' ConnectionStrings__Migrations='<conn>' \
dotnet run --project src/ArchiveBridge.ControlPlane
```

## Telas (1440×900, Modo de Demonstração)

| # | Tela | Arquivo |
|---|---|---|
| 1 | Login | [`screenshots/1-login.png`](screenshots/1-login.png) |
| 2 | Dashboard (Visão Geral) | [`screenshots/2-dashboard.png`](screenshots/2-dashboard.png) |
| 3 | Projeto | [`screenshots/3-projeto.png`](screenshots/3-projeto.png) |
| 4 | Onda | [`screenshots/4-wave.png`](screenshots/4-wave.png) |
| 5 | Enterprise Vault | [`screenshots/5-enterprise-vault.png`](screenshots/5-enterprise-vault.png) |
| 6 | Evidências | [`screenshots/6-evidencias.png`](screenshots/6-evidencias.png) |
| 7 | Mapping | [`screenshots/7-mapping.png`](screenshots/7-mapping.png) |
| 8 | Auditoria | [`screenshots/8-auditoria.png`](screenshots/8-auditoria.png) |
| 9 | Jobs | [`screenshots/9-jobs.png`](screenshots/9-jobs.png) |
