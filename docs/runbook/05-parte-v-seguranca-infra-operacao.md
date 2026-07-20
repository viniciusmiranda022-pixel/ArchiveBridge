<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

# Parte V - Segurança, infraestrutura e operação

## 30. Threat model e ativos

### 30.1 Ativos críticos

- PST/EV source content;
- parts e artefatos reparados;
- SAS Purview e import URLs Graph;
- chaves/HMAC de fingerprint;
- certificados de connectors e workloads;
- mapping de identidade owner → mailbox;
- manifestos, resultados e evidências;
- permissões Exchange/Purview/Graph;
- pipeline, pacotes e imagens de worker.

### 30.2 Ameaças principais

| **Ameaça** | **Exemplo** | **Controle obrigatório** |
| --- | --- | --- |
| Spoofing | connector falso | mTLS, enrollment único, workload identity |
| Tampering | trocar part após validação | SHA-256, immutability, storage lease, WORM evidence |
| Repudiation | operador nega import | Entra identity, approval, ledger, audit log |
| Information disclosure | SAS em log | secret redaction, no transcript, JIT worker |
| Denial of service | PST malformado consome RAM/IO | limites, timeout, worker isolado, quota por tenant |
| Elevation of privilege | worker PST acessa Exchange | identidades separadas, RBAC mínimo, network boundary |
| Cross-tenant access | API retorna job alheio | tenant key, RLS, authorization tests |
| Supply-chain | pacote PST adulterado | lock file, SBOM, assinatura, scanning, private registry |
| Queue poisoning | payload manipulado | schema, HMAC quando necessário, IDs apenas, inbox |
| Path traversal | nome PST escreve fora da pasta | canonical path e raiz allowlisted |

## 31. Identidade e segregação de funções

| **Persona/Workload** | **Pode** | **Não pode** |
| --- | --- | --- |
| ProjectAdmin | criar projeto e atribuir equipe | iniciar import ou acessar conteúdo |
| MigrationEngineer | preparar, inspecionar, planejar | aprovar a própria onda |
| MigrationApprover | aprovar plan/wave/close | alterar artefato |
| M365Operator | operar portal e anexar resultados | editar plan/manifest |
| Auditor | ler evidência e relatórios | executar jobs |
| SecurityAdmin | política, incident response | aprovar fidelidade funcional sozinho |
| PST Worker MI | ler landing, escrever work/parts | SQL amplo, Key Vault Purview, Exchange |
| Upload Worker MI | ler parts aprovadas e SAS JIT | ler original/quarantine, administrar tenant |
| Recon Worker MI | ler metadados e EXO stats | escrever part ou iniciar import |
| Evidence Signer MI | ler manifestos finais e assinar | processar PST/importar |

As identidades não compartilham secret. Managed Identity é preferida em Azure. Source Connector usa certificado por instalação. Exchange unattended usa app-only CBA/managed identity conforme documentação e role mínimo.

## 32. Segredos e material criptográfico

- Key Vault com soft delete e purge protection.
- public network access desabilitado; private endpoint.
- acesso por RBAC, não access policy legado.
- segredo SAS com content type, expiry e tags; sem valor em tag.
- chaves de HMAC por tenant, versionadas; rotação não invalida fingerprints antigos porque `keyVersion` é persistida.
- assinatura de evidence package com chave não exportável; Managed HSM se exigência justificar.
- certificate renewal automatizado e alarmes 30/14/7 dias.
- nenhum segredo em variável de pipeline persistente quando workload identity resolve.

### 32.1 Redaction central

Toda telemetria passa por redactor que remove:

- query string de URL;
- Authorization e cookies;
- UPN/SMTP em claro, substituindo por tenant HMAC;
- UNC real, usando source ID;
- subject/body/attachment name;
- stack data de SDK que inclua payload.

Teste unitário injeta canaries e falha se aparecerem em log serializado.

## 33. Storage e ciclo de vida

| **Container** | **Mutável** | **Acesso** | **Retenção sugerida** |
| --- | --- | --- | --- |
| `landing` | não após hash | PST worker read | até aprovação + rollback |
| `work` | sim, temporário | PST worker rw | auto-delete após 7–30 dias |
| `parts` | não após validação | validator/upload read | até fechamento + rollback |
| `quarantine` | append/new version | especialistas JIT | contrato/legal |
| `evidence` | WORM | signer write, auditor read | 7–10 anos ou política |
| `reports` | versionado | operadores | projeto + retenção |

Todos usam encryption at rest; CMK quando exigido. Storage account não permite shared key para aplicações normais. Private endpoint e firewall deny by default. Versioning/soft delete complementam WORM, mas não substituem política imutável.

## 34. Hardening dos workers Windows

- Windows Server suportado e patchado; imagem imutável reconstruída mensalmente e para CVE crítico.
- Defender for Endpoint ativo; tamper protection.
- App Control for Business/WDAC allowlist.
- PowerShell Constrained Language para contas não administrativas quando compatível.
- JEA para operações de suporte.
- RDP desabilitado por padrão; Bastion/JIT/PIM para break-glass.
- Credential Guard, BitLocker, Secure Boot e vTPM.
- SMBv1/TLS legado desabilitados.
- serviços rodam em virtual service account/gMSA local quando aplicável; nunca Domain Admin.
- scratch com ACL exclusiva, sem execute para arquivos de dados.
- outbound apenas destinos necessários.
- crash dump tratado como sensível; coleta desabilitada ou armazenada com proteção equivalente.
- worker é reimaginado após job de alto risco ou manipulação de SAS.

## 35. Malware e conteúdo hostil

PST e anexos são dados não confiáveis. O produto não executa macros, scripts ou previews. Extração de anexo só ocorre quando necessário à validação aprovada, em diretório `noexec`/ACL, com scan e limite. HTML nunca é renderizado no portal sem sanitização rigorosa; a UI padrão não mostra corpo.

Itens detectados não são automaticamente descartados: a política define se migram, ficam em quarentena ou exigem decisão de segurança. O relatório registra hash e disposition, não redistribui malware.

## 36. Infraestrutura como código

### 36.1 Recursos por ambiente

- resource group por ambiente e região;
- VNet, subnets, NSG, route table, firewall;
- Private DNS zones e private endpoints;
- Azure Container Registry Premium;
- Container Apps Environment interno ou App Service Environment conforme decisão;
- Azure SQL Database/server com private endpoint, TDE e auditing;
- Service Bus Premium;
- Storage accounts separados para dados e evidência;
- Key Vault/Managed HSM;
- Log Analytics, Application Insights, Azure Monitor workspace;
- VM Scale Sets Windows para workers;
- Bastion e management resources;
- Defender for Cloud e policy assignments;
- Recovery Services/backup conforme recurso.

### 36.2 Comandos de bootstrap

```powershell
az login --tenant <TENANT_ID>
az account set --subscription <SUBSCRIPTION_ID>

$location = 'brazilsouth'
$environment = 'dev'
$rg = "rg-pstmig-$environment-$location"

az group create --name $rg --location $location --tags `
  application=pst-migration environment=$environment dataClassification=confidential

az deployment sub what-if `
  --location $location `
  --template-file .\infra\bicep\main.bicep `
  --parameters .\infra\bicep\environments\dev.bicepparam

az deployment sub create `
  --name "pstmig-$environment-$(Get-Date -Format yyyyMMddHHmmss)" `
  --location $location `
  --template-file .\infra\bicep\main.bicep `
  --parameters .\infra\bicep\environments\dev.bicepparam
```

Produção nunca usa `az deployment ... create` da estação do desenvolvedor. O comando é executado pelo pipeline com OIDC, approval e artifact assinado.

### 36.3 Esqueleto do Bicep

```bicep
targetScope = 'subscription'

@allowed(['dev', 'test', 'prod'])
param environment string
param location string
param workloadName string = 'pstmig'

var rgName = 'rg-${workloadName}-${environment}-${location}'

resource rg 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: rgName
  location: location
  tags: {
    application: workloadName
    environment: environment
    dataClassification: 'confidential'
    managedBy: 'bicep'
  }
}

module network './modules/network.bicep' = {
  scope: rg
  name: 'network'
  params: {
    environment: environment
    location: location
  }
}

module data './modules/data.bicep' = {
  scope: rg
  name: 'data'
  params: {
    environment: environment
    location: location
    privateEndpointSubnetId: network.outputs.privateEndpointSubnetId
  }
}
```

As API versions precisam ser revisadas na data da implementação. O documento não congela versões Bicep futuras sem validação.

### 36.4 Azure Policy gates

- negar public IP em NIC/VMSS;
- negar Storage/SQL/Key Vault/Service Bus com public access;
- exigir TLS mínimo e HTTPS only;
- exigir diagnostic settings;
- exigir tags;
- restringir regiões;
- exigir Defender/antimalware;
- negar shared key/local auth quando suportado;
- exigir private endpoints para recursos de dados;
- auditar CMK e purge protection.

## 37. CI/CD e supply chain

### 37.1 Pipeline de pull request

92. checkout por SHA;
93. verificar assinatura/branch policy;
94. `dotnet restore --locked-mode`;
95. SAST, secret scanning e IaC scanning;
96. build Release com deterministic builds;
97. unit + architecture tests;
98. integration tests em recursos efêmeros;
99. cobertura e mutation testing nos módulos críticos;
100. SBOM CycloneDX/SPDX;
101. dependency vulnerability scan;
102. container scan;
103. Bicep lint/build/what-if;
104. gerar artifacts, provenance e assinatura;
105. publicar somente para registry privado.

### 37.2 Pipeline de promoção

- build uma vez, promover o mesmo digest;
- dev automático após merge;
- test exige testes de compatibilidade e corpus sintético;
- staging exige change record;
- prod exige dois aprovadores, evidence de testes, rollback plan e janela;
- post-deploy smoke tests e observação;
- rollback automático somente para control plane stateless; migração de schema é expand/contract.

### 37.3 Comandos de qualidade no pipeline

```powershell
dotnet restore PstMigration.slnx --locked-mode
dotnet build PstMigration.slnx -c Release --no-restore /p:ContinuousIntegrationBuild=true
dotnet test PstMigration.slnx -c Release --no-build `
  --collect:"XPlat Code Coverage" `
  --logger "trx;LogFileName=test-results.trx"
dotnet format PstMigration.slnx --verify-no-changes --no-restore
dotnet list PstMigration.slnx package --vulnerable --include-transitive
az bicep build --file infra/bicep/main.bicep
```

## 38. Configuração de aplicação

Configuração não secreta é versionada. Segredo é referenciado por URI/identidade. Exemplo:

```json
{
  "Processing": {
    "TargetPartBytes": 19327352832,
    "HardPartBytes": 20000000000,
    "MaxConcurrentHeavyWorkers": 2,
    "CheckpointIntervalItems": 10000,
    "SourceStabilityWindowSeconds": 60
  },
  "Purview": {
    "MaxCsvRows": 500,
    "MainArchiveImportLimitBytes": 107374182400,
    "DefaultTargetRootPrefix": "/ImportedPst",
    "RequirePortalApproval": true
  },
  "Security": {
    "RejectTargetRootSlash": true,
    "AllowRawMessagePreview": false,
    "RequireDualApprovalForImport": true
  }
}
```

Os valores de limite ficam no provider capability registry e possuem fonte/data; não devem ser tratados como eternos. Mudança precisa de PR + compatibility test.

## 39. Observabilidade

### 39.1 Logging estruturado

Campos obrigatórios: timestamp UTC, level, event name, tenant HMAC, projectId, jobId, artifactId, waveId, attemptId, workerInstance, correlationId, traceId, duration, bytes, itemCount, outcome, errorCode. Proibidos: subject, body, attachment name, SAS, token e path real.

### 39.2 Métricas

- queue depth, age e DLQ count;
- worker heartbeat, CPU, memory, disk, IOPS, throughput;
- bytes/items por stage;
- stage duration p50/p95/p99;
- retry rate e error class;
- source/part hash mismatch;
- quota/capability blocks;
- evidence completeness;
- EXO/Purview observed throughput por mailbox;
- cost allocation por tenant/project.

### 39.3 Tracing

OpenTelemetry propaga `traceparent` do API → outbox → Service Bus → worker. Operações de hashing/partitioning usam spans agregados; não criar span por item em milhões de itens. Eventos de item ficam em contadores/checkpoints.

### 39.4 Alertas iniciais

| **Alerta** | **Condição** | **Severidade** |
| --- | --- | --- |
| DLQ growing | \>0 por 15 min | Sev2 |
| Worker lost | heartbeat ausente 5 min com lease ativo | Sev2 |
| Disk pressure | \<20% livre ou scratch abaixo do necessário | Sev2 |
| Hash mismatch | qualquer | Sev1 |
| Cross-tenant denial | qualquer tentativa | Sev1 segurança |
| Secret leak canary | qualquer match | Sev1 segurança |
| Import stalled | sem mudança além da baseline do provider | Sev3/2 |
| Evidence incomplete | job tenta fechar | bloqueio |
| Certificate expiry | \<30 dias | Sev3; \<7 Sev2 |

## 40. SLO, RTO e RPO

| **Serviço** | **SLI** | **SLO** |
| --- | --- | --- |
| Control API | requests válidos bem-sucedidos | 99,9% mensal |
| Orchestrator | comandos iniciados dentro de 5 min | 99% |
| Evidence | eventos confirmados persistidos | 100% |
| Worker | job retomável após falha | 99,9%; exceções de fonte explicadas |
| Portal | páginas críticas disponíveis | 99,9% |

RPO controle ≤5 min; evidence event confirmado RPO 0 lógico. RTO controle ≤4 h. PST/parts são reconstruíveis apenas antes de import; após import, parts devem existir em storage redundante durante rollback.

## 41. Backup e disaster recovery

- Azure SQL point-in-time restore e geo-redundant backup conforme ambiente.
- Storage de artifacts com redundância definida por região e requisito.
- Evidence WORM replicada quando suporte/legal permitirem.
- Bicep e configuração no Git; imagens no ACR secundário quando necessário.
- Service Bus DR não é assumido como replicação completa de mensagens sem capability confirmada; estado do domínio permite reconstruir comandos pendentes.
- export diário de `pending-work snapshot` assinado.
- teste trimestral de restauração SQL e semestral de failover completo.

### 41.1 Ordem de recuperação

106. declarar incidente e congelar novas ondas;
107. restaurar rede/identidade/Key Vault;
108. restaurar SQL e validar ledger sequence;
109. conectar storage e verificar amostra de hashes;
110. recriar Service Bus e repopular a partir do estado pendente;
111. restaurar control API/orchestrator;
112. iniciar workers sem auto-consume;
113. reconciliar leases e attempts;
114. liberar queues por stage;
115. emitir incident evidence e obter aprovação para retomar imports.

## 42. Runbooks operacionais

### 42.1 Worker caiu durante partição

116. não iniciar segunda execução imediatamente;
117. verificar heartbeat, VM state e lease;
118. preservar work directory e logs;
119. confirmar se part parcial foi publicado; part sem `VALIDATED` não é consumível;
120. fechar attempt como `Interrupted`;
121. excluir output parcial somente após hash/evidência e por job de limpeza aprovado;
122. iniciar nova attempt a partir do source imutável e plan hash;
123. comparar parts já concluídas; reutilizar apenas hash idêntico;
124. registrar causa e capacidade.

### 42.2 AzCopy falhou

125. capturar job ID/exit code/log sanitizado;
126. verificar rede, DNS, espaço e expiração do SAS;
127. não imprimir SAS;
128. usar plan/resume somente conforme versão AzCopy homologada;
129. confirmar lista de blobs/resultado;
130. se SAS expirou, obter novo pelo portal e criar nova secret version;
131. manter mesmo subfolder, filenames e hashes;
132. gerar novo attempt, não nova part;
133. atualizar custody event.

### 42.3 Mapping CSV falhou validação

134. baixar validation report;
135. anexar ao wave;
136. parser classifica linha/campo;
137. corrigir fonte de dados, não CSV manual;
138. gerar mapping version N+1;
139. calcular hash e diff sem PII exposta;
140. revalidar;
141. invalidar versão antiga para uso, preservando-a como evidência.

### 42.4 Import parece travado

142. comparar duração com baseline típica aproximada de 24 GB/dia por mailbox, sem tratar como garantia;
143. verificar concorrência de múltiplos PSTs para a mesma mailbox;
144. verificar tamanho de cada part e quota disponível;
145. coletar status por PST;
146. não reiniciar job por ansiedade;
147. após threshold operacional, abrir suporte com pacote;
148. não mudar TargetRootFolder no retry.

### 42.5 Itens skipped/corrupted

149. importar relatório do serviço;
150. localizar part e source lineage;
151. separar erro de serviço de corrupção de fonte;
152. se reparo permitido, criar artifact derivado;
153. revalidar;
154. reimportar o mesmo PST reparado no mesmo target root somente após análise de duplicidade;
155. reconciliar e aprovar disposition dos itens irrecuperáveis.

### 42.6 Suspeita de segredo em log

156. Sev1 segurança;
157. revogar/rotacionar SAS/token/certificado;
158. bloquear acesso ao log;
159. identificar alcance e downloads;
160. remover da visualização normal sem destruir evidência legal;
161. atualizar redactor/testes;
162. reconstruir worker e revisar pipeline;
163. comunicar conforme plano de incidente/LGPD.
