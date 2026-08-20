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
| Roubo/força bruta de senha | PBKDF2-HMAC-SHA256, sal por usuário, 210k iterações, verificação em tempo constante. Usuário inexistente ainda executa uma derivação PBKDF2 dummy (equalização de timing — não revela existência do login). Falhas auditadas; mensagem genérica. **Rate limiting fechado no Passo 8** (política "login": 5 requisições/minuto por endereço remoto). |
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

## Delta — Passo 6A: backend seguro de recebimento e validação de CSV de mapping

Novo backend **somente de recepção/validação** (sem endpoint HTTP ainda). Recebe um `Stream` externo não
confiável, calcula custódia e valida contra a onda autorizada; **não importa nada para o Microsoft 365** e
**não retém os bytes brutos**.

**Ativos:** o CSV enviado (transitório, não persistido), o **SHA-256 dos bytes exatos**, o `ValidationId`, o
snapshot da onda (versão/hashes), os problemas de validação e os metadados de custódia.

**Classificação de dados (custódia).** A tentativa persistida **contém metadados operacionais de identidade** —
`UserId`, `RequestedBy` (normalizado: `Trim`, 1..200, sem controle) e `DisplayFileName` (basename sanitizado) —
além de hashes e contagens; portanto **não é "zero PII"**, é evidência operacional atribuível. O que ela **não**
contém: os bytes brutos do CSV, mailbox, caminho físico, nome de PST ou qualquer valor de célula do mapping.

**Entradas não confiáveis:** bytes do `Stream`, `DeclaredLength`, nome de arquivo do cliente, `WaveId`,
`ContentCodePage`, chave de idempotência.

| Ameaça | Controle |
| --- | --- |
| Upload gigante / exaustão de memória | Leitura **bounded** pelo conteúdo real (`limit + 1`); teto **absoluto e constante** de 50 MiB (`AbsoluteMaxUploadBytes`), **não parametrizável** — o limite efetivo é sempre validado `0 < efetivo <= 50 MiB` no construtor, nenhum chamador pode elevá-lo; nunca confia no `Content-Length` — `DeclaredLength` é só preflight (negativo ⇒ rejeição imediata). Oversized ⇒ rejeição **pré-custódia** (zero tentativa). |
| Amplificação do parser | O parser materializa no máximo `cabeçalho + MaxDataRows + 1` registros; entrada com milhões de linhas produz deterministicamente `rows-exceeded` sem explodir memória/contagem de problemas. |
| CSV malformado / confusão de encoding / BOM | Decodificação **UTF-8 estrita SEM BOM** (`throwOnInvalidBytes`); qualquer BOM (UTF-8/16/32) e bytes inválidos ⇒ **Rejected** (custodiado, sem fallback 1252, sem "correção"). CSV estruturalmente inválido ⇒ fail-closed. |
| CSV/formula injection | Preserva a regra existente: primeiro caractere `= + - @` TAB CR ⇒ inválido; **nunca** reescreve/sanitiza o valor autorizado; os seis gatilhos são testados. |
| Path traversal via nome de arquivo | O nome do cliente é DADO: apenas o **basename normalizado** (sem componentes de caminho, sem controle, ≤ 260) vira metadado de exibição; jamais caminho físico nem chave de autorização. |
| IDOR cross-project / cross-tenant na onda | A onda é resolvida **server-side** por `IWaveStore` (RLS + `project_id = @project`); onda de outro projeto/tenant ⇒ NotFound (indistinguível), zero tentativa. FK composta `(wave_id, wave_version, tenant_id, project_id)` reforça o escopo fisicamente. |
| Fonte mutável | Validação só contra onda **Approved/Frozen** (fonte imutável). Outros estados ⇒ precondição pré-custódia, zero tentativa. |
| TOCTOU da onda | O store **revalida** a onda (versão/hashes/estado) na MESMA transação da inserção; divergência ⇒ *stale/concurrency*, zero tentativa. `Approved → Frozen` (versão/hashes intactos) permanece válido. |
| Replay / colisão de idempotência | Chave obrigatória e não nula; busca sob lock de range + índice único como backstop. Mesma chave + mesmo conteúdo/contexto ⇒ replay: o resultado é montado a partir da **evidência canônica persistida** (a tentativa ORIGINAL, relida na mesma transação) — **nunca** do valor recalculado nesta execução, de modo que o `ValidationId` histórico é imune à evolução posterior do validador. Qualquer divergência (bytes/onda/versão/hash/code page) ⇒ conflito, uma única tentativa. `Guid.Empty` recusado no use case E no store. |
| Vazamento de PII nos erros | Problemas persistidos carregam apenas código/linha/coluna/mensagem genérica — nunca mailbox, caminho, PST, valor de célula bruto ou nome do cliente. |
| Amplificação da lista de erros | Teto `MaxPersistedValidationIssues` (default 1000); acima dele a lista é truncada deterministicamente (`IssuesTruncated`), nunca explodindo tabela/memória. |
| Normalização antes do hash | O SHA-256 é sobre os **bytes exatos recebidos** — nunca sobre texto reserializado/normalizado; alterar um byte irrelevante muda o hash. |

**Custódia append-only:** `mapping_validation_attempts` + `mapping_validation_issues` recebem apenas
`SELECT/INSERT` para a aplicação (sem `UPDATE`/`DELETE`); a identidade de **manutenção não tem grant algum**.
RLS por tenant (FILTER + BLOCK AFTER INSERT) e filtro explícito por projeto. Migration `0018` aditiva/append-only
(`0001–0017` intocadas).

**Risco residual:** os **bytes brutos do upload NÃO são retidos** neste sub-incremento — a custódia guarda o
SHA-256 e os metadados de validação. A retenção de bytes para eventual importação é decisão posterior e **não**
autoriza importação no Microsoft 365. Nenhum endpoint de upload foi exposto ainda (o hardening HTTP/multipart/
antiforgery é do incremento seguinte).

## Fora do escopo (fail-closed por ausência)

Nenhuma execução de `Export-EVArchive`, PST, Purview, Microsoft Graph, AzCopy ou ingestão no Microsoft 365.

O backend de **recepção/validação de CSV (Passo 6A) já existe** nas camadas Application/Infrastructure e
persiste tentativas de validação (custódia append-only). O que **ainda não existe** é a **superfície HTTP/Portal
de upload** — endpoint `POST`, `IFormFile`/multipart, antiforgery e rate-limit específicos de upload — que
pertence ao incremento seguinte (6B) e será modelada ao ser implementada. As demais ações de escrita
(aprovações, disparo de descoberta, administração de usuários) também ainda não têm superfície web nesta fatia.

## Higiene de logs

Nenhuma senha, hash, cookie ou segredo é registrado. A auditoria de autenticação grava apenas login,
resultado, motivo curto não sensível, endereço remoto, o escopo quando conhecido
(tenant/projeto/usuário) e um `correlation_id` por tentativa.

## Delta — Passo 8: hardening, rate limiting, observabilidade e empacotamento on-premises

Fechamento do Slice 4A: nenhum write-path novo, nenhuma tela nova, nenhuma migration. Endurece a superfície
HTTP já existente e fecha os itens que o próprio threat model já sinalizava como pendentes.

**Rate limiting.** As quatro operações POST sensíveis do Slice 4A (login, solicitar descoberta EV, validar
CSV de mapping, solicitar retry de job) passam a ter limite de requisições. Implementado como middleware
dedicado (`SensitiveOperationRateLimitingMiddleware`) em vez do atributo nativo `[EnableRateLimiting]`: Razor
Pages resolve o HANDLER específico (`OnPostXxxAsync`) **depois** do roteamento de endpoint, então um atributo
aplicado a um handler não é observável pelo middleware de rate limiting nativo (que decide a partir dos
metadados do ENDPOINT — a página inteira, não o handler). O middleware dedicado reconhece as quatro
rotas/handlers por caminho + verbo + `?handler=`, com a mesma granularidade pretendida:

| Política | Partição | Limite | Rotas |
| --- | --- | --- | --- |
| `login` | endereço remoto | 5/min | `POST /Account/Login` |
| `sensitive-write` | usuário autenticado (`PortalClaims.UserId`) | 20/min | `POST /EnterpriseVault/Index?handler=RequestDiscovery`, `POST /Mapping/Index?handler=ValidateCsv`, `POST /Jobs/Details?handler=Retry` |

Partições em memória (janela fixa, sem enfileiramento — estourar responde `429` imediatamente), consistente
com a arquitetura de instância única on-premises aprovada — sem persistência, sem nova migration. O rate
limiter roda ANTES da autorização/antiforgery/handler; uma requisição limitada nunca toca o store.

**Correlation ID por requisição.** `RequestCorrelationMiddleware` é o primeiro middleware do pipeline: atribui
(ou propaga, se o cliente já enviar `X-Correlation-Id` plausível — nunca usado para autorização/escopo) um
identificador por requisição, devolvido no cabeçalho de resposta `X-Correlation-Id` e exibido na página de
erro (`/Error`) para o operador referenciar ao suporte. Cobre toda requisição, inclusive redirecionamentos
HTTPS e o caminho de exceção não tratada (`UseExceptionHandler`).

**Logs estruturados e métricas.** O mesmo middleware registra uma linha de log estruturada por requisição
(método, rota, status, duração, correlation ID — nunca corpo, query string, cookie, senha ou segredo) e
alimenta `ControlPlaneMetrics` (`System.Diagnostics.Metrics`, sem exportador obrigatório): contagem de
requisições, contagem de falhas (`status >= 400`) e histograma de latência, com tags de método e classe de
status apenas — nenhuma tag de negócio/identidade.

**Health check de indisponibilidade (verificado por teste).** `/health/ready` já retornava `503` quando o SQL
Server obrigatório está inacessível (Passo 4A); o Passo 8 acrescenta o teste automatizado que prova esse
caminho (`Slice4aHealthReadyUnavailableTests`), fechando a lacuna de cobertura — não havia, antes, nenhum
teste que provasse o lado "indisponível". `/health/live` continua independente do banco.

**Empacotamento on-premises.** `builder.Host.UseWindowsService()` foi adicionado (pacote
`Microsoft.Extensions.Hosting.WindowsServices`, sem dependência de Azure/SaaS): permite hospedar o Control
Plane como Windows Service; é NO-OP fora do Windows/fora de execução como serviço — não afeta `dotnet run`,
`WebApplicationFactory` (testes) nem a hospedagem via IIS (que usa o módulo ASP.NET Core, caminho
independente). Runbook de implantação em `docs/engineering/control-plane-onprem-deployment-runbook.md`.

**Risco residual.** Autenticidade criptográfica do cursor keyset (HMAC/Data Protection) permanece fora deste
Passo — como já registrado no delta do Passo 5, é hardening opcional que exige decisão sobre
persistência/rotação de chaves e comportamento multi-instância; nada no cursor concede autoridade hoje (ver
delta do Passo 5). Rate limiting é em memória por instância — um deployment futuro com múltiplas instâncias
atrás de um load balancer precisaria de um backend compartilhado (fora do escopo de instância única aprovado).

## Delta — Frente UX/UI (Client Demo)

Frente **estritamente visual** (reveste o Portal em linguagem de produto enterprise). **Nenhuma proteção
existente foi removida ou relaxada**: `[Authorize]`/fallback policy, RBAC, antiforgery, escopo tenant/projeto,
RLS, idempotência, auditoria, validação de evidência, fail-closed, HTTPS/HSTS/cookie Secure em produção e a
**CSP restrita** permanecem intactos. Toda a estilização vive em `wwwroot/css/site.css`; o JS mínimo em
`wwwroot/js/site.js`; ícones são SVG inline — **sem** `style=`/`<style>`/`on*` inline e **sem CDN**, coerente
com `style-src 'self'; script-src 'self'`.

**Modo de Demonstração (Presentation Mode)** — faceta de UI, `Enabled` default `false`:

- **Fail-closed no startup**: habilitado fora de `Development`/`Staging` **aborta o processo**; Produção nunca
  serve dados simulados.
- **Zero escrita de negócio**: provedor em memória, somente leitura, vivendo apenas no `ControlPlane`
  (verificado por teste de arquitetura). A única ação de escrita do portal (solicitar descoberta EV) é
  **recusada** (`403`) antes de tocar qualquer store quando o modo está ativo. Não cria SQL, Job, evidência,
  tentativa de validação; não chama Worker/PowerShell.
- **Dataset 100% sintético** (Contoso Demo), nunca misturado com dados reais; banner âmbar em todas as telas.

**Encoder HTML**: `WebEncoderOptions` passa a usar `UnicodeRanges.All` — apenas para renderizar letras
acentuadas como UTF-8 literal em vez de entidades numéricas. Os caracteres significativos para HTML
(`< > & " '`) **continuam sempre codificados**; a proteção contra XSS não muda.
