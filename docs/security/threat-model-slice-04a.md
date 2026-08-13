# Threat model — Slice 4A (Control Plane API e Portal Operacional)

Delta sobre o modelo de ameaças da plataforma. Escopo: a superfície **web** (portal on-premises, somente
leitura) e a **identidade do portal**. Não há execução de exportação nem integração externa nesta fatia.

## Ativos

- **Credenciais do portal** (hash de senha, nunca em claro) e **sessão** (cookie de autenticação).
- **Metadados de governança/observabilidade** por tenant (projetos, ondas, jobs, descoberta EV, hashes de
  evidência). **Não** há segredo, SAS, token, PST ou evidência bruta no read-model.
- **Trilha de auditoria** de autenticação.

## Ameaças e mitigações

| Ameaça | Mitigação |
| --- | --- |
| Acesso não autenticado a dados | Política de autorização **fail-closed** (fallback exige usuário autenticado); só login/erro/health são anônimos. |
| Elevação de privilégio (papel) | RBAC por papel; Administração exige `Administrator` e a trilha de auditoria exige `Auditor`/`Administrator` (política + `AuthorizeFolder`). Catálogo de papéis fechado por FK/CHECK no banco. |
| Vazamento cross-tenant | As leituras de negócio ocorrem sob `SESSION_CONTEXT('tenant_id')` do usuário (RLS). A auditoria de login **não** está sob RLS (tabela de identidade) e é isolada por **filtro explícito** `tenant_id = @tenant`. Tenant vem da claim, nunca do cliente. Testes comprovam o isolamento (negócio e auditoria). |
| Vazamento cross-project (IDOR) | Além da RLS por tenant, toda leitura de negócio filtra `project_id = @project` do usuário. Pedir transições de um job de outro projeto do mesmo tenant retorna vazio. Comprovado por teste tenant A/projeto 1 × tenant A/projeto 2. |
| Roubo/força bruta de senha | PBKDF2-HMAC-SHA256, sal por usuário, 210k iterações, verificação em tempo constante. Usuário inexistente ainda executa uma derivação PBKDF2 dummy (equalização de timing — não revela existência do login). Falhas auditadas; mensagem genérica. Rate limiting: próximo incremento. |
| Sequestro de sessão | Cookie `HttpOnly`, `SameSite=Lax`; `SecurePolicy=Always` fora de dev. Fora de desenvolvimento, `UseHsts()`+`UseHttpsRedirection()` tornam o HTTPS obrigatório (fail-closed). Expiração deslizante de 8 h. |
| CSRF | Antiforgery em todos os POST (login/logout). |
| XSS / injeção de conteúdo externo | Página **autocontida** + **CSP** `default-src 'self'` (sem CDN/script externo); saída Razor codificada por padrão. Cabeçalhos `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`. |
| SQL injection | Todo acesso a dados é parametrizado (ADO com `SqlParameter`). |
| Exposição de segredo em repositório | Sem senha versionada: strings de conexão on-premises usam *Integrated Security* (sem senha); bootstrap exige senha injetada pelo ambiente (vazia ⇒ desabilitado). |
| Simulação enganosa de capacidade | Telas de capacidades ausentes (exportação/disparo/download/gestão de usuários) declaram indisponibilidade honesta; nenhum botão executa mock. |

## Fora do escopo (fail-closed por ausência)

Nenhuma execução de `Export-EVArchive`, PST, Purview, Microsoft Graph, AzCopy ou ingestão no Microsoft 365.
As ações de escrita (aprovações, upload/validação de CSV, disparo de descoberta, administração de usuários)
não existem nesta fatia e serão modeladas ao serem implementadas.

## Higiene de logs

Nenhuma senha, hash, cookie ou segredo é registrado. A auditoria de autenticação grava apenas login,
resultado, motivo curto não sensível, endereço remoto, o escopo quando conhecido
(tenant/projeto/usuário) e um `correlation_id` por tentativa.
