# Slice 4A — Worker EV operacional (sub-incremento 2)

Consumidor durável e seguro dos comandos de descoberta Enterprise Vault. Fecha o
laço **SQL durable queue → pending scope → worker → processor → discovery →
evidência + SQL → Job terminal/retry** sem nenhuma superfície de disparo no
Portal (o Portal continua somente leitura; o botão/POST vem depois).

## Fluxo de composição real

```
appsettings (EnterpriseVaultDiscovery:Enabled=true)
        │  fail-closed no startup: config inválida OU host não-Windows ⇒ o host não sobe
        ▼
EnterpriseVaultDiscoveryComposition.Configure(HostApplicationBuilder)
        │
        ├─ SqlEvDiscoveryPendingScopeReader ──(identidade de MANUTENÇÃO, READ-ONLY)
        │        enumera tenant/projeto com Job EV elegível — nada além do escopo
        │
        └─ EvDiscoveryCommandProcessor  ◄── grafo REAL reutilizado:
                 SystemClock, TenantConnectionFactory,
                 SqlEvDiscoveryCommandInbox, SqlJobStore, SqlJobLeaseManager,
                 SqlProjectStore, SqlEvDiscoveryStore,
                 FileSystemEvDiscoveryEvidenceStore, EvDiscoveryEvidenceSerializer,
                 AdapterCompatibilityEvaluator(EvExportDocumented151Adapter),
                 PowerShellEvCapabilityDiscovery → WindowsEvPowerShellHost,
                 DiscoverEvCapabilitiesUseCase, EvDiscoveryPolicy.Default
        ▼
EvDiscoveryWorker  (BackgroundService, caminho normal)
  loop: scopes = reader.ListEligibleScopesAsync(MaxScopesPerPoll)
        foreach scope: processor.ProcessNextAsync(scope, workerId, lease, correlation)
        delay(PollInterval)
```

`SyntheticEvJobWorker` é uma ferramenta de diagnóstico ISOLADA (classe própria):
`SyntheticJobMode:Enabled=false` por padrão e bloqueada em `Production`. Não
interfere no worker operacional.

## Fronteira de segurança da identidade de manutenção (STOP-THE-LINE)

A identidade de manutenção (`OpenForMaintenanceAsync`) é usada **exclusivamente**
por `SqlEvDiscoveryPendingScopeReader` para responder "há trabalho elegível neste
tenant/projeto?" — uma consulta `SELECT DISTINCT` estritamente READ-ONLY que não
retorna site, directory server, solicitante, hashes, evidência nem conteúdo.

A partir do `TenantScope` descoberto, **todo** o processamento volta à identidade
normal da aplicação (`OpenForTenantAsync` → RLS por tenant + filtro por
`project_id`). A enumeração de manutenção **não é autorização**: o claim, a
execução, a persistência e a conclusão continuam confinados ao escopo pela RLS e
pelo filtro de projeto. É proibido usar manutenção para claim/alteração/attempt/
processamento/persistência/publicação/finalização/retry.

Um teste arquitetural garante que `OpenForMaintenanceAsync` só aparece no leitor
de escopos dentro de `Infrastructure/EnterpriseVault`, e um teste de integração
prova que, após a enumeração, o processamento de um escopo não alcança o comando
de outro.

## Configuração (fail-closed)

```yaml
EnterpriseVaultDiscovery:
  Enabled: false            # padrão: worker operacional não executa
  PollIntervalSeconds: 5    # > 0
  LeaseSeconds: 30          # > 0
  MaxScopesPerPoll: 32      # 1..512
  EvidenceRoot: ""          # obrigatório quando Enabled=true
ConnectionStrings:
  Application: ""           # obrigatória quando Enabled=true
  Maintenance: ""           # obrigatória quando Enabled=true
```

- `Enabled=false` ⇒ o worker operacional não é registrado.
- `Enabled=true` com `Application`/`Maintenance`/`EvidenceRoot` vazio, ou
  `PollInterval`/`Lease`/`MaxScopesPerPoll` inválidos ⇒ falha de startup.
- `Enabled=true` fora de um Windows Worker ⇒ falha explícita (`PlatformNotSupportedException`);
  nunca "Ready" com sonda falsa em produção. Nenhuma senha/credencial é versionada.

## Limites

`Export-EVArchive` não é executado (apenas seus metadados são observados);
`LaboratoryValidated` permanece `false`; nenhuma integração de Slice 4B
(AzCopy, Purview, Microsoft Graph, Exchange Online, PST, libpff) é introduzida.
PowerShell nunca executa no processo web. O worker chama somente
`EvDiscoveryCommandProcessor.ProcessNextAsync` — sem execução inline.
