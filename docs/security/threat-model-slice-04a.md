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

## Delta — Passo 5: paginação keyset e filtros (histórico de evidências e auditoria)

Superfícies **somente leitura** que passam a aceitar novas entradas não confiáveis do navegador: `cursor`,
`pageSize`, prefixo de site, prefixo de usuário, `ActionCode`, `Reason`, `CorrelationId`, intervalo de datas,
`EnvironmentId` e filtros de status.

| Ameaça | Controle |
| --- | --- |
| SQL injection via filtro | SQL **estático** com predicados opcionais parametrizados (`@x IS NULL OR col = @x`); todo valor é `SqlParameter`. Zero concatenação de entrada do usuário. Sem ordenação dinâmica (`?orderBy` não existe). |
| Abuso de curinga (`%` `_` `[`) | Prefixos usam `LIKE @p ESCAPE N'\'` com escaping literal server-side; o curinga de prefixo é anexado pelo servidor. Curingas do usuário são DADOS, nunca sintaxe. Teste com `%`, `_`, `[`, `\`, `' OR 1=1 --`. |
| Truncamento do padrão escapado reintroduzindo curinga | O escaping pode dobrar cada metacaractere (`\ % _ [` ⇒ 2 chars) e o servidor anexa `%`, logo o pior caso é `rawMax*2+1`. O parâmetro `NVARCHAR` é dimensionado por `SqlLikePattern.MaxEscapedPrefixLength(rawMax)` (SitePrefix `201`; UsernamePrefix `401`) — **nunca** `NVARCHAR(rawMax)` — para que o padrão jamais trunque no boundary e o escaping permaneça literal. Teste de boundary com prefixo no comprimento máximo saturado de metacaracteres. |
| Cursor oversized / fora do alfabeto / Base64/JSON malformado | Cursor é envelope **versionado**, URL-safe **estrito**, decodificado com fail-closed: acima do teto de tamanho (`2048`, checado ANTES de qualquer alocação), qualquer caractere fora de `A–Z a–z 0–9 - _` (padding `=` e `+`/`/` recusados), Base64/JSON inválido, versão desconhecida ou posição malformada ⇒ `400` (nunca `500`, nenhuma query com valor parcial). |
| Reuso de cursor com filtros divergentes | O cursor carrega o **fingerprint** canônico dos filtros; mismatch ⇒ `400`. É consistência, não autenticidade: o fingerprint **não é MAC** e um cliente deliberado pode forjar cursor+fingerprint — o que NÃO concede autoridade (ver linha seguinte). |
| Cursor forjado tentando trocar escopo | O cursor **não** carrega tenant/projeto e **não é autorização**. Uma posição de seek fabricada só desloca o ponto de ordenação DENTRO do resultado já autorizado; tenant/projeto são sempre re-resolvidos do principal (RLS + `project_id = @project`). Teste explícito: cursor bem-formado construído a partir de dados de A/P2 ou B, usado por um principal de A/P1, nunca revela A/P2 nem B. |
| Bypass de escopo por tenant/projeto | O cursor **não** carrega escopo. Tenant/projeto são sempre re-resolvidos do principal. Evidência e auditoria operacional: RLS por tenant **+** `project_id = @project` explícito (inclusive no JOIN). |
| Vazamento cross-tenant na autenticação | `portal_sign_in_events` está fora da RLS; a busca aplica `tenant_id = @tenant` obrigatório (tenant nunca vem do cliente). Eventos com `tenant_id` nulo (usuário inexistente) não aparecem. |
| Exaustão de recursos por page size | `pageSize` do cliente nunca controla o `TOP` sem limite: clamp determinístico para `[1, 100]` server-side; `TOP (@pageSize + 1)` (a linha extra só decide `HasMore`, sem `COUNT(*)`). |
| Duplicatas/lacunas sob inserção concorrente | Paginação **keyset/seek** com ordenação fixa e desempate determinístico (`event_id`; `environment_id`+`discovery_version`). Sem `OFFSET`. Teste de inserção entre páginas: nenhuma linha reaparece. |
| Recursão de auditoria | Visualizar/filtrar/paginar auditoria é read-only e **não** gera evento operacional. |

**Controles:** parsing tipado; comprimentos limitados; parâmetro `LIKE` dimensionado pelo pior caso do escaping
(sem truncamento no boundary); SQL parametrizado; keyset pagination; ordenação fixa server-side; teto de page
size; teto de tamanho do cursor + alfabeto Base64Url estrito (recusa antes de alocar); filter fingerprint (de
consistência, não de autenticidade); escopo derivado do principal; RLS + filtro de projeto explícito; filtro de
tenant explícito na autenticação; desempates determinísticos; índices de seek (migration `0017`, aditiva);
`HTTP 400` fail-closed. O **download verificado** de `evidence.json` permanece inalterado — o Passo 5 só melhora
a listagem.

**Cursor (documentação):** o cursor é **entrada NÃO confiável**. Base64Url **não é assinatura** e o fingerprint
**não é MAC** — um cliente deliberado pode fabricar um cursor bem-formado com qualquer posição de seek. Isso é
aceitável e **não concede autoridade**: o cursor **não contém** tenant/projeto — apenas as chaves de ordenação,
a versão do schema e o fingerprint dos filtros — e **não é autorização**. O controle de segurança real é a
cadeia `principal autenticado → re-resolução de tenant/projeto → RLS → filtro de projeto explícito → parâmetros
SQL → posição de seek apenas dentro desse resultado autorizado`. Uma posição forjada só desloca o ponto
temporal da consulta já escopada; jamais troca o escopo. Autenticidade criptográfica do cursor (HMAC/Data
Protection), se decidirmos exigi-la, é **hardening do Passo 8** — exige decisão sobre persistência/rotação de
chaves, restart e multi-instância — e **não** faz parte deste passo.

## Fora do escopo (fail-closed por ausência)

Nenhuma execução de `Export-EVArchive`, PST, Purview, Microsoft Graph, AzCopy ou ingestão no Microsoft 365.
As ações de escrita (aprovações, upload/validação de CSV, disparo de descoberta, administração de usuários)
não existem nesta fatia e serão modeladas ao serem implementadas.

## Higiene de logs

Nenhuma senha, hash, cookie ou segredo é registrado. A auditoria de autenticação grava apenas login,
resultado, motivo curto não sensível, endereço remoto, o escopo quando conhecido
(tenant/projeto/usuário) e um `correlation_id` por tentativa.
