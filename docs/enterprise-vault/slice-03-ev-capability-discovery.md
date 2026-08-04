# Vertical Slice 3 — Enterprise Vault Capability Discovery

Descoberta **read-only** das capacidades técnicas de um ambiente Enterprise Vault (EV) **antes** de
qualquer exportação. Responde, de forma auditável: qual ambiente foi consultado, qual versão foi
observada, quais componentes/módulos/snap-ins/cmdlets existem, se `Export-EVArchive` está de fato
disponível e com qual assinatura, quais permissões estão presentes/ausentes, quais capacidades o
adapter pode declarar, qual adapter é selecionado, e se o ambiente está **Ready**, **Blocked** ou
**Unsupported** — sempre sustentado por evidência.

## 1. Escopo e não-escopo

- **Read-only:** nenhuma modificação no Enterprise Vault; nenhum dado/config alterado.
- **Sem exportação:** a slice DESCOBRE o cmdlet (`Get-Command`), mas **nunca** o executa; não cria,
  lê ou transfere PST.
- **Fora de escopo:** exportação real, PST, AzCopy, Purview, Microsoft Graph, M365, Exchange Online,
  libpff, reconciliação, portal completo, migração ponta a ponta, automação destrutiva.
- **Seleção por capacidades, não por versão textual:** a versão observada é evidência, não decisão.

## 2. Arquitetura da descoberta

```
DiscoverEvCapabilities (comando durável, workload EnterpriseVault)
  → claim EXCLUSIVO (EXISTS ev_discovery_commands, fencing por época)
  → guarda de contexto (versão/hash de config + política) — STALE_COMMAND_CONTEXT se divergir
  → heartbeat PERIÓDICO (lease/3) durante toda a operação
  → IEvCapabilityDiscovery.ProbeAsync  (read-only, via IEvPowerShellExecutor controlado)
        → EvDiscoveryObservation (fatos por capacidade + assinatura observada)
  → AdapterCompatibilityEvaluator (todos os IEvVersionAdapter) → AdapterSelectionPolicy
        → EvAdapterSelection (Supported / Blocked / Unsupported / NotFound / Ambiguous)
  → EvDiscoveryEvaluator → EvDiscoveryRunResult (capacidades + status + result code)
  → evidência canônica (determinística) → StageAsync → ReserveAsync (tx1) → PublishAsync (fora do SQL)
        → FinalizeAsync (tx2) — promove Pending → terminal, superseder a anterior
  → complete / fail / retry (fencing)
```

Camadas (hexagonal): **Domain** (modelo de capacidades, máquina de estados, política de seleção,
hashes) → **Contracts** (portas: `IEvPowerShellExecutor`, `IEvCapabilityDiscovery`, `IEvVersionAdapter`,
`IEvDiscoveryStore`, `IEvDiscoveryEvidenceStore`, `IEvDiscoveryCommandInbox`) → **Application** (use case,
avaliador de adapter, adapters concretos, processador durável) → **Infrastructure** (SQL, evidência em
filesystem, executor controlado, descoberta PowerShell).

## 3. Modelo de capacidades

Tipado, sem flags soltas nem strings livres:

- `EvEnvironmentIdentity` — ambiente observado (id/site/servidor/versão observada/versão de produto/fonte).
- `EvCapability` — `CapabilityCode` (tipado) + `CapabilityVersion` + `Availability` + `EvidenceReference`
  + `BlockingReason`.
- `EvCapabilitySet` — ambiente + adapter + esquema + capacidades + `ConfigurationHash` + `EvidenceHash` +
  `Status`.
- `EvExportSignature` — assinatura NORMALIZADA (nome/módulo/tipo/parâmetros/obrigatórios/conjuntos +
  hash determinístico da forma).

Estados de capacidade: `Available`, `Unavailable`, `Indeterminate`, `PermissionDenied`, `Unsupported`.
Estados da avaliação: `Pending`, `Discovering`, `Ready`, `Blocked`, `Unsupported`, `Failed`,
`Superseded` (transições fail-closed em `EvDiscoveryTransitions`).

As 17 capacidades avaliadas: `EV_POWERSHELL_AVAILABLE`, `EV_MODULE_AVAILABLE`, `EV_SNAPIN_AVAILABLE`,
`EV_DIRECTORY_CONNECTIVITY`, `EV_SITE_DISCOVERY`, `EV_SERVER_DISCOVERY`, `EV_VAULT_STORE_DISCOVERY`,
`EV_ARCHIVE_DISCOVERY`, `EV_EXPORT_CMDLET_AVAILABLE`, `EV_EXPORT_CMDLET_SIGNATURE_SUPPORTED`,
`EV_EXPORT_PST_SUPPORTED`, `EV_EXPORT_SIZE_PARAMETER_SUPPORTED`, `EV_EXPORT_REPORT_SUPPORTED`,
`EV_EXPORT_FILTERING_SUPPORTED`, `EV_STAGING_PATH_ACCESS`, `EV_REQUIRED_PERMISSIONS`,
`EV_VERSION_SUPPORTED_BY_ADAPTER`.

> **Regra fundamental:** uma capacidade só é `Available` com **evidência positiva**. Ausência de
> evidência resulta em `Indeterminate` (ou `Unavailable`/`PermissionDenied`), nunca `Available` por
> inferência da versão.

## 4. Política de seleção de adapter

Determinística e fail-closed (`AdapterSelectionPolicy`):

- nenhum adapter reconhece ⇒ `NotFound`;
- 1 compatível ⇒ `Supported`;
- vários compatíveis com **uma única maior precedência** ⇒ `Supported` (o de maior precedência,
  registrado em `ev_adapter_evaluations`);
- vários compatíveis **empatados** na maior precedência ⇒ `Ambiguous` (fail-closed);
- nenhum compatível, algum `Blocked` ⇒ `Blocked`; todos `Unsupported` ⇒ `Unsupported`.

Cada adapter (`IEvVersionAdapter`) reconhece um conjunto COMPROVADO de capacidades e valida
compatibilidade pela **assinatura observada** (não pela versão textual). Não existe adapter genérico
que alegue suportar todas as versões do EV. Nesta slice há dois adapters de referência: **moderno**
(assinatura com `OutputPath` + tamanho/relatório/filtragem) e **legado** (`ExportPath`).

## 5. Execução PowerShell controlada

`IEvPowerShellExecutor` (impl. `GuardedEvPowerShellExecutor`) delega a um `IEvPowerShellHost` específico
de SO **somente após** impor:

- allowlist explícita de comandos (fora dela ⇒ recusa, host nunca chamado);
- recusa de `Invoke-Expression`;
- parâmetros TIPADOS (nome = identificador; valor sem `; | & `` ` `` $( ${` nova linha/redirecionamento);
- timeout obrigatório e positivo (estouro ⇒ `TimedOut`, fail-closed);
- limite de tamanho de saída (truncamento sinalizado);
- sanitização de segredos na saída (`password=/token=/sas=` → `[REDACTED]`);
- execução não interativa; working directory controlado; compatível com Windows Worker isolado.

Nunca armazena senha/token/credencial/conteúdo de mensagem/PST; nunca concatena entrada em script.

## 6. Dados coletados vs. não coletados

- **Coletados (metadados/evidência):** identidade do ambiente, versão observada, presença de
  módulo/snap-in/cmdlet, assinatura normalizada, contagens de sites/servidores/vault stores/archives,
  permissões presentes/ausentes, acesso ao caminho de staging.
- **NÃO coletados:** conteúdo de mensagem/PST, credenciais, tokens/SAS, dados pessoais de mailbox.

## 7. Persistência (SQL + evidência)

SQL guarda **apenas metadados**: `ev_environments`, `ev_discovery_commands`, `ev_discovery_runs`
(âncora de versão + ciclo de vida + hashes + adapter + caminho lógico + timestamps), `ev_capabilities`,
`ev_adapter_evaluations`, `ev_discovery_findings` (append-only), e as projeções `ev_capability_sets` /
`ev_discovery_evidence`. Migration **aditiva e protegida por hash** `0011`; RLS por tenant; FKs
compostas `(…, tenant_id, project_id)`; `rowversion`; índice único filtrado da versão corrente. O único
`UPDATE` permitido à aplicação em `ev_discovery_runs` é da coluna `status`.

A evidência detalhada é um **artefato imutável** (`evidence.json` + `evidence.sha256` + `manifest.json`)
publicado por rename atômico de diretório, versionado por ambiente. O mesmo padrão da Slice 2:
staging → publicação imutável → reconciliação por hashes → nenhuma sobrescrita silenciosa. Uma nova
descoberta cria N+1 e só marca a anterior como `Superseded` **após** a nova estar completa e validada.

## 8. Códigos de resultado (estruturados)

`EV_DISCOVERY_COMPLETED`, `EV_DISCOVERY_PARTIAL`, `EV_DISCOVERY_FAILED`, `EV_CONNECTIVITY_FAILED`,
`EV_DIRECTORY_UNREACHABLE`, `EV_MODULE_NOT_FOUND`, `EV_SNAPIN_NOT_FOUND`, `EV_CMDLET_NOT_AVAILABLE`,
`EV_CMDLET_SIGNATURE_UNSUPPORTED`, `EV_PERMISSION_INSUFFICIENT`, `EV_VERSION_UNSUPPORTED`,
`EV_CAPABILITY_INDETERMINATE`, `EV_ADAPTER_NOT_FOUND`, `EV_ADAPTER_AMBIGUOUS`, `EV_DISCOVERY_TIMEOUT`,
`EV_DISCOVERY_OUTPUT_INVALID`, `EV_DISCOVERY_EVIDENCE_INVALID`, `STALE_COMMAND_CONTEXT`, `FENCED_OUT`.
O controle de fluxo usa esses códigos tipados, nunca mensagens textuais.

## 9. Permissões (privilégio mínimo)

A descoberta roda com **privilégio mínimo** e **não exige Domain Admin**. As permissões mínimas
conhecidas para as sondas read-only são: leitura do Directory do EV, enumeração de sites/servidores/
vault stores/archives, e presença dos módulos/snap-ins do EV no host de execução.

> **Pendente de laboratório:** o conjunto EXATO de permissões mínimas e os nomes precisos dos
> módulos/snap-ins/parâmetros do `Export-EVArchive` por versão do EV dependem de prova em laboratório
> com produto Veritas real. Estão marcados como pendentes e **não** são declarados como suportados sem
> essa evidência.

## 10. Procedimento de laboratório

1. Provisionar um ambiente EV real (versão-alvo) com uma conta de serviço de privilégio mínimo.
2. Registrar o `IEvPowerShellHost` de produção (Windows) e habilitar a categoria de teste de
   laboratório (gated por variável de ambiente explícita — desabilitada por padrão no CI).
3. Executar a descoberta e conferir a evidência publicada contra o ambiente.
4. Fixar a assinatura observada de `Export-EVArchive` e o conjunto de permissões mínimas por versão;
   promover os itens pendentes deste documento.

## 11. Critérios de aceite

- descoberta read-only; nenhuma exportação executada;
- capacidades comprovadas por evidência (nunca por versão textual);
- adapter escolhido por capabilities, determinístico e registrado; ausência/ambiguidade falha fechado;
- contexto durável versionado; Jobs, fencing e heartbeat reutilizados;
- evidência imutável persistida; isolamento tenant/projeto provado (RLS + FKs compostas);
- migrations aditivas protegidas por hash; testes aprovados;
- nenhum suporte genérico a todas as versões; nenhuma integração Purview/Graph/PST/AzCopy/libpff.

## 12. Pendências para o Slice de exportação (futuro)

- Execução real de `Export-EVArchive` para gerar PST (fora desta slice).
- Assinaturas e permissões confirmadas em laboratório por versão do EV.
- Adapters de exportação (produção de parts), reconciliação e o portal — sob nova evidência e decisão.
