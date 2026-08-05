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
  → IEvCapabilityDiscovery.ProbeAsync  (read-only, sondas TIPADAS via IEvPowerShellHost + EvProbeExecutor fail-closed)
        → EvDiscoveryObservation (fatos por capacidade + assinatura observada)
  → AdapterCompatibilityEvaluator (todos os IEvVersionAdapter) → AdapterSelectionPolicy
        → EvAdapterSelection (Supported / Blocked / Unsupported / NotFound / Ambiguous)
  → EvDiscoveryEvaluator → EvDiscoveryRunResult (capacidades + status + result code)
  → TRÊS hashes (configuração completa + evidência semântica completa + SHA-256 do conteúdo)
  → evidência canônica (determinística) → StageAsync → ReserveAsync ATÔMICA (tx1) → PublishAsync (fora do SQL)
        → FinalizeAsync (tx2) — valida os três hashes, promove Pending → terminal, superseder a anterior
  → complete / fail / retry (fencing)
```

Camadas (hexagonal): **Domain** (modelo de capacidades, máquina de estados, política de seleção,
perfil/maturidade da assinatura, hashes/impressões digitais) → **Contracts** (portas: `IEvPowerShellHost`,
`IEvCapabilityDiscovery`, `IEvVersionAdapter`, `IEvDiscoveryStore`, `IEvDiscoveryEvidenceStore`,
`IEvDiscoveryCommandInbox`) → **Application** (use case, avaliador de adapter, adapters concretos,
processador durável) → **Infrastructure** (SQL, evidência em filesystem, host PowerShell tipado com scripts
internos fixos + executor fail-closed, descoberta PowerShell).

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
que alegue suportar todas as versões do EV. Nesta slice há **um** adapter de referência, para o **perfil
oficial documentado do EV 15.1** (`EV_EXPORT_PROFILE_DOCUMENTED_15_1`): identifica-se pela assinatura
documentada de `Export-EVArchive` — `ArchiveId` + `OutputDirectory` + `Format` — e documenta ainda
`SearchString`, `MaxThreads`, `Retry` e `MaxPSTSizeMB`. **Não há** parâmetro `GenerateReport`: o relatório
de exportação é gerado **automaticamente** (`ExportReport_<datetime>.txt`), portanto
`EV_EXPORT_REPORT_SUPPORTED` **não** depende de parâmetro. A maturidade do perfil é registrada
**separadamente** — `RuntimeObserved` (forma observada em runtime), `OfficialDocumentation` (bate com a
documentação oficial) e `LaboratoryValidated` (homologado com produto real). Esta slice **nunca** declara
`LaboratoryValidated`: é `RuntimeObserved`/`OfficialDocumentation`, `LaboratoryValidated = false`.

## 5. Host PowerShell tipado (sondas fixas, fail-closed)

Não há comando arbitrário nem `CommandName` livre. `IEvPowerShellHost` recebe uma **sonda TIPADA**
(`EvPowerShellProbeKind`: `PowerShellEnvironment`, `RegisteredEvSnapin`, `AvailableEvModule`, `EvSite`,
`EvServer`, `EvVaultStore`, `EvArchive`, `ExportEvArchiveCommandMetadata`, `StagingPathAccess`) e a mapeia
para um **script interno FIXO, imutável e versionado** (catálogo). Os scripts usam apenas comandos
documentados de leitura (`Get-Command`, `Get-EVSite`, `Get-EVServer`, `Get-EVVaultStore`, `Get-EVArchive`,
`Test-Path`) e emitem um **envelope JSON versionado** `{schemaVersion, probe, success, errorCode, data}`.
**Nenhuma** sonda executa `Export-EVArchive` — a de metadados apenas lê a assinatura via `Get-Command`.

O `WindowsEvPowerShellHost` (Windows Worker) monta o processo com `EvPowerShellCommandBuilder`
(`-NoProfile -NonInteractive -NoLogo`, script interno fixo, working directory controlado, PATH restrito ao
diretório do executável, **argumentos tipados por variável de ambiente** `EV_PROBE_ARG_*` — nunca na linha
de comando, nunca concatenados, sem credencial) e o executa com `ByteLimitedProcessRunner` (contagem em
**bytes reais** UTF-8, limite por stream, drenagem concorrente sem deadlock, processo morto ao exceder o
limite ou o timeout). **A prova contra Enterprise Vault real é pendente de laboratório**: fora do Windows o
host lança `PlatformNotSupportedException`, enquanto o construtor de comando e o runner byte-limitado
permanecem testáveis de forma portável.

O `EvProbeExecutor` valida **fail-closed antes de interpretar qualquer dado**: recusa argumentos com
metacaracteres de injeção (`; | & `` ` `` $( ${` nova linha/redirecionamento, `Invoke-Expression`) sem
chamar o host; e então recusa `TimedOut`, limite de saída excedido, `ExitCode != 0`, `stderr` não vazio
(política explícita), `stdout` vazio e envelope inválido (schema desconhecido, propriedade obrigatória
ausente, tipo errado, `probe` divergente, `data` não-objeto). Cada sonda produz seu **próprio** resultado
(status + evidência + código + categoria de erro): a falha ou `PermissionDenied` de uma sonda **não**
colapsa as demais.

Nunca armazena senha/token/credencial/conteúdo de mensagem/PST; nunca concatena entrada em script.

## 6. Dados coletados vs. não coletados

- **Coletados (metadados/evidência):** identidade do ambiente, versão observada, presença de
  módulo/snap-in/cmdlet, assinatura normalizada, contagens de sites/servidores/vault stores/archives,
  permissões presentes/ausentes, acesso ao caminho de staging.
- **NÃO coletados:** conteúdo de mensagem/PST, credenciais, tokens/SAS, dados pessoais de mailbox.

## 7. Persistência (SQL + evidência)

SQL guarda **apenas metadados**: `ev_environments`, `ev_discovery_commands`, `ev_discovery_runs`
(âncora de versão + ciclo de vida + **três hashes** + adapter + caminho lógico + timestamps),
`ev_capabilities`, `ev_adapter_evaluations` (com `profile_id` + maturidade `runtime_observed` /
`official_documentation` / `automated_fixture_validated` / `laboratory_validated`), `ev_discovery_findings`
(append-only, com `error_category`), e as projeções `ev_capability_sets` / `ev_discovery_evidence`.
Migrations **aditivas e protegidas por hash** `0011` (base), `0012` (coluna `content_sha256`, índice único
filtrado de reserva pendente por impressão digital, colunas de perfil/maturidade e `error_category`) e
`0013` (coluna `automated_fixture_validated` — validação por fixtures automatizados, distinta de
laboratório); RLS por tenant; FKs compostas `(…, tenant_id, project_id)`; `rowversion`. O único `UPDATE`
permitido à aplicação em `ev_discovery_runs` é da coluna `status`.

Uma reserva é identificada pelos **três hashes**: `configuration_hash` (configuração completa — ambiente,
versão/hash de config do projeto, versão da política, capacidades exigidas ordenadas, limites, versões de
esquema/catálogo), `evidence_hash` (**evidência semântica completa** — identidade integral do ambiente,
versão observada, capacidades, assinatura normalizada, avaliações de adapter/precedência/perfil, achados,
status e código) e `content_sha256` (bytes do `evidence.json`). Qualquer diferença semântica muda o hash e
**não** reaproveita uma reserva incompatível. A reserva é **ATÔMICA**: na mesma transação, sob lock,
verifica a pendente equivalente pelos três hashes e a insere (ou a reutiliza) — um **índice único filtrado**
`UX_evd_pending_fingerprint` (status Pending) impede duas versões Pending para a mesma evidência, e uma
colisão concorrente é reconciliada relendo a pendente. A finalização revalida os três hashes antes de
promover.

**Invariantes de unicidade (alinhadas ao SQL).** `AdapterId` é ÚNICO por execução (constraint
`UQ_eveval_adapter`) e `CapabilityCode` é ÚNICO por conjunto — no capability set (`UQ_evcap_code`) e nas
capacidades declaradas de cada avaliação. `EvDiscoveryInvariants.Validate` recusa duplicidades **fail-closed**
com exceção estruturada (`EvDiscoveryInvariantException`, sem depender da mensagem do SQL), e é aplicada em
TODOS os caminhos (`EvCapabilitySet.Create`, `AdapterSelectionPolicy`, o fingerprint, o serializer e a store
ANTES de abrir a transação) — uma duplicidade jamais chega ao `INSERT` (nunca vira `SqlException`
2601/2627) e a versão anterior permanece intacta; mesmo um record público construído diretamente é recusado
pelo hash/serializer/persistência.

**Canonicalização (codificação tipada, sem sentinela).** O `EvDiscoverySemanticFingerprint` é o SHA-256
calculado DIRETAMENTE sobre a codificação canônica de `EvDiscoveryCanonical`: cada campo carrega **tipo**
explícito e **tag**; cada string é **length-prefixed** (comprimento UTF-8 + bytes UTF-8); listas gravam a
**quantidade**; inteiros/enums vão em binário; anuláveis têm **marcador de presença**. Não há separador nem
string sentinela — qualquer valor (inclusive contendo `U+001F`) é representado sem ambiguidade. **Não
existem sentinelas de ausência:** `null` ≠ `""` ≠ `0` ≠ `"<none>"` são estados DISTINTOS (AdapterId/
AdapterVersion do capability set, adapter selecionado, `ProfileId`, `BlockingReason`, `CapabilityCode` de
achado, assinatura e maturidade). Como `AdapterId`/`CapabilityCode` são únicos, a ordenação é ordinal por
essas chaves; o serializer usa a MESMA ordenação, e inverter coleções válidas nunca altera hash nem bytes.
O `evidence.json` e o `SemanticEvidenceHash` representam exatamente as mesmas distinções semânticas: **todo
campo factual da assinatura entra no hash em posição fixa** — inclusive `ObservedVersion`, que é registrado
no `evidence.json` (duas assinaturas iguais em tudo, exceto `ObservedVersion`, produzem hash, `evidence.json`
e `ContentSha256` diferentes). Fica de fora do hash e do artefato apenas o que é genuinamente **volátil**,
como `DiscoveredAtUtc` (que reside nos metadados SQL, não na evidência canônica).

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
> essa evidência. A lista completa de pendências e seu status
> (`IMPLEMENTED_WITH_FIXTURES` / `LAB_VALIDATION_PENDING`) está em
> [`ev-lab-validation-backlog.md`](./ev-lab-validation-backlog.md) — essas pendências **não** bloqueiam
> a slice: a estratégia aprovada é implementar por contratos/fixtures e homologar contra EV real depois.

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
