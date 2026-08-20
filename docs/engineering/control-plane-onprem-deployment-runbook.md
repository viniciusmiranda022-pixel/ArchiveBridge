# Runbook — Implantação e operação on-premises do Control Plane (Slice 4A, Passo 8)

> Escopo: `ArchiveBridge.ControlPlane` (portal + API do plano de controle). **On-premises**: sem Azure App
> Service, sem banco em nuvem, sem SaaS da ArchiveBridge, sem comunicação externa além das integrações
> explicitamente configuradas (SQL Server, storage de evidência). Baseline aprovada: **IIS** ou **Windows
> Service/Kestrel** — ambas suportadas; escolha uma por ambiente.

## 1. Pré-requisitos

- Windows Server com **.NET 10 Hosting Bundle** instalado (necessário mesmo para Windows Service — o host
  ainda depende do runtime ASP.NET Core; para IIS o Hosting Bundle também instala o **ASP.NET Core Module
  v2**).
- SQL Server on-premises acessível pela rede do host, com o schema já provisionado (ver §3, Migrations).
- Volume/pasta (local ou SMB) dedicado à raiz de evidências (`ControlPlane:EvidenceRoot`), com ACL restrita
  à conta de serviço do Control Plane (somente ela deve gravar; leitura pode ser mais ampla para backup).
- Certificado TLS válido para o hostname do portal (terminado no IIS ou diretamente no Kestrel, conforme a
  opção escolhida em §2).

## 2. Hospedagem

### Opção A — IIS (recomendado quando o ambiente já opera IIS)

1. Publique com `dotnet publish -c Release` (o publish gera o `web.config` padrão do SDK Web, que configura
   o **ASP.NET Core Module v2** como reverse proxy — nenhuma configuração manual adicional é necessária para
   o roteamento HTTP).
2. Crie um Application Pool dedicado, modo **No Managed Code** (o CLR gerenciado é o do .NET, não o do IIS).
3. Aponte o site para a pasta de publish; vincule o certificate TLS (site binding HTTPS).
4. Configure as variáveis de ambiente/`appsettings.Production.json` (§3) no Application Pool ou no
   `web.config` (`<environmentVariables>` dentro de `<aspNetCore>`) — nunca versionadas no repositório.
5. TLS é terminado no IIS; o processo do Control Plane escuta em loopback (padrão do módulo ASP.NET Core).

### Opção B — Windows Service (Kestrel)

1. Publique com `dotnet publish -c Release` (self-contained ou framework-dependent, conforme a política de
   runtime do ambiente).
2. `builder.Host.UseWindowsService()` (Passo 8) já habilita a integração — o executável publicado roda como
   serviço sem código adicional. Registre o serviço:
   ```powershell
   sc.exe create ArchiveBridgeControlPlane binPath= "C:\ArchiveBridge\ControlPlane\ArchiveBridge.ControlPlane.exe" start= auto
   sc.exe description ArchiveBridgeControlPlane "ArchiveBridge Control Plane (Slice 4A)"
   ```
3. Configure a conta de serviço com privilégio mínimo (acesso à raiz de evidências e à rede do SQL Server;
   nenhum privilégio administrativo é necessário).
4. TLS: fora de Development, `UseHsts()` + `UseHttpsRedirection()` exigem HTTPS — vincule o certificado ao
   binding do Kestrel (`Kestrel:Endpoints:Https:Certificate` em configuração, ou um proxy reverso dedicado
   — ex.: IIS em modo reverse-proxy puro, ou outro terminador TLS on-premises já operado pelo ambiente).
5. Inicie o serviço: `sc.exe start ArchiveBridgeControlPlane`; pare com `sc.exe stop ArchiveBridgeControlPlane`.

Em ambos os casos, `dotnet run`/execução direta do `.dll` continua funcionando normalmente para
desenvolvimento e para os testes de integração (`WebApplicationFactory`) — `UseWindowsService()` é **NO-OP**
fora do Windows/fora de execução como serviço gerenciado pelo SCM.

## 3. Configuração obrigatória

Nenhuma senha/segredo é versionado no repositório. Defina via variável de ambiente (dupla-underscore para
seções aninhadas, ex. `ConnectionStrings__Application`) ou `appsettings.Production.json` fora do controle de
versão, no host de destino:

| Chave | Obrigatória | Descrição |
| --- | --- | --- |
| `ConnectionStrings:Application` | sim | identidade da APLICAÇÃO (RLS por tenant). |
| `ConnectionStrings:Maintenance` | sim | identidade de MANUTENÇÃO (sem grant de escrita de negócio). |
| `ConnectionStrings:Migrations` | somente se `RunMigrationsAtStartup=true` | identidade com privilégio de DDL — nunca a de aplicação. |
| `ControlPlane:EvidenceRoot` | sim (default `data/evidence`) | raiz on-premises dos bundles imutáveis de evidência (local ou UNC). |
| `ControlPlane:BootstrapAdmin:Password` | não (vazio = desabilitado) | provisiona o admin inicial no primeiro start; **vazio em produção após o primeiro provisionamento**. |
| `EnterpriseVaultDiscovery:Enabled`, `MappingUpload:Enabled`, `JobRetry:Enabled` | não (default `false`) | feature gates locais das três ações de escrita — fail-closed por padrão. |
| `PresentationMode:Enabled` | não (default `false`) | **nunca** `true` fora de Development/Staging — o processo aborta o startup se violado. |

### Migrations

O único mecanismo de aplicação é `ControlPlane:RunMigrationsAtStartup` (usa `ConnectionStrings:Migrations` —
identidade com privilégio de DDL, nunca a de aplicação). Trate-o como um passo de implantação **explícito e
controlado**, não como comportamento permanente de produção:

1. Antes do deploy (ou em janela de manutenção), suba a aplicação **uma vez** com
   `ControlPlane:RunMigrationsAtStartup=true` (variável de ambiente ou configuração temporária) contra o SQL
   Server de destino. `MigrationRunner` aplica as migrations pendentes sob lock de aplicação
   (`sp_getapplock`), de forma idempotente — reexecutar não duplica nem falha se já estiver em dia.
2. Confirme nos logs de startup quais versões foram aplicadas (ou nenhuma, se já estava em dia).
3. **Depois**, redefina `ControlPlane:RunMigrationsAtStartup=false` para os starts normais do serviço — isso
   evita que toda reinicialização do processo tente DDL contra o banco.

As migrations são aditivas e verificadas por hash (`MigrationHashTests`, executado no CI a cada PR); nenhuma
migration histórica é alterada por este processo.

## 4. Health checks e monitoramento

- **Liveness** — `GET /health/live` (anônimo): prova que o processo está no ar. Não depende do SQL Server.
  Configure o probe de liveness do orquestrador/monitoramento (ou o Service Recovery do Windows Service)
  contra esta rota.
- **Readiness** — `GET /health/ready` (anônimo): `200 {"status":"ready"}` quando o SQL Server obrigatório
  responde; `503 {"status":"unavailable"}` caso contrário (fail-closed — nunca declara pronto sem o banco).
  Use esta rota para o probe de readiness/balanceamento de carga (não para restart automático — uma
  indisponibilidade transitória de banco não deve reiniciar o processo).
- **Correlation ID** (Passo 8) — toda resposta carrega o cabeçalho `X-Correlation-Id`; a página `/Error`
  exibe o mesmo valor ao operador. Para localizar a linha de log correspondente a um incidente relatado,
  busque pelo correlation ID nos logs estruturados do host (stdout do serviço/IIS, ou o coletor de logs já
  operado pelo ambiente).
- **Métricas** (Passo 8) — publicadas via `System.Diagnostics.Metrics` sob o meter
  `ArchiveBridge.ControlPlane` (`archivebridge_controlplane_http_requests_total`,
  `archivebridge_controlplane_http_failures_total`, `archivebridge_controlplane_http_request_duration_ms`).
  Sem exportador obrigatório — colete localmente com `dotnet-counters monitor --process-id <pid> --counters
  ArchiveBridge.ControlPlane` para uma verificação rápida, ou conecte um coletor OpenTelemetry/Prometheus
  on-premises já operado pelo ambiente (decisão de infraestrutura, fora do escopo desta fatia).

## 5. Backup e retenção

- **Banco de dados**: siga a política de backup já operada para o SQL Server on-premises do ambiente (fora
  do escopo desta fatia — o Control Plane não gerencia backup do SQL Server).
- **Raiz de evidências** (`ControlPlane:EvidenceRoot`): os bundles são **imutáveis** após publicação (Slice
  3) — um backup incremental padrão (ex. cópia do volume/UNC) é suficiente; não há necessidade de
  coordenação transacional com o banco além de preservar `content_sha256`/`evidence_path` já ancorados no
  SQL.

## 6. Checklist de validação pós-implantação

1. `GET /health/live` → `200`.
2. `GET /health/ready` → `200` (com o SQL Server acessível); force uma indisponibilidade momentânea (ex.
   pausar o serviço SQL em um ambiente de homologação) e confirme `503` — nunca `200` sem o banco.
3. Login com o admin de bootstrap funciona; **em seguida, limpe `ControlPlane:BootstrapAdmin:Password`** (o
   bootstrap só roda quando o portal está vazio, mas manter a senha configurada é desnecessário após o
   primeiro provisionamento).
4. Confirme HTTPS obrigatório: uma requisição HTTP direta ao host (fora do Development) é redirecionada.
5. Confirme rate limiting: 6 tentativas de login consecutivas do mesmo IP retornam `429` na sexta (Passo 8).
6. Confirme que os feature gates de escrita (`EnterpriseVaultDiscovery`, `MappingUpload`, `JobRetry`)
   refletem a decisão operacional pretendida para o ambiente (todos `false` por padrão — fail-closed).
