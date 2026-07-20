<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

# Parte IV - Destinos Microsoft 365

## 24. Strategy e capability gates

`ITargetIngestor` expõe capabilities e prechecks antes de aceitar uma onda. O planner não supõe que “Microsoft 365” é uma única capacidade.

```csharp
public interface ITargetIngestor
{
    TargetProvider Provider { get; }

    Task<TargetCapabilities> DiscoverCapabilitiesAsync(
        TenantContext tenant,
        CancellationToken cancellationToken);

    Task<TargetPrecheckResult> PrecheckAsync(
        ImportWave wave,
        CancellationToken cancellationToken);

    Task<PreparedDestinationPackage> PrepareAsync(
        ImportWave wave,
        CancellationToken cancellationToken);

    Task<DestinationObservation> ObserveAsync(
        DestinationRun run,
        CancellationToken cancellationToken);
}
```

O retorno de `DiscoverCapabilitiesAsync` precisa distinguir `GeneralAvailability`, `Preview`, `Contractual`, `Unsupported` e `Unknown`. `Unknown` bloqueia. A evidência da decisão inclui URL oficial, data, versão da documentação, tenant testado e resultado.

## 25. Adapter Purview Network Upload - caminho GA

Este é o adapter habilitado no primeiro release. Ele prepara e transporta; a criação/início do job permanece no portal Purview porque a Microsoft orienta usar a interface.

### 25.1 Permissões mínimas

Para criar jobs, a documentação requer `Mailbox Import Export` e `Mail Recipients`, ou Global Administrator. O produto exige um role group dedicado e rejeita Global Administrator como conta operacional normal.

```powershell
Install-Module ExchangeOnlineManagement -Scope CurrentUser
Import-Module ExchangeOnlineManagement
Connect-ExchangeOnline -UserPrincipalName <ADMIN_APROVADO>

$roleGroupName = 'PST Import Operators'
if (-not (Get-RoleGroup -Identity $roleGroupName -ErrorAction SilentlyContinue)) {
    New-RoleGroup `
      -Name $roleGroupName `
      -Roles 'Mailbox Import Export','Mail Recipients' `
      -Members '<OPERADOR_1>','<OPERADOR_2>'
}

Get-RoleGroup -Identity $roleGroupName |
    Format-List Name,Roles,Members

Disconnect-ExchangeOnline -Confirm:$false
```

As contas devem usar MFA, Conditional Access, dispositivo compatível e PIM/JIT quando a organização suportar. O aprovador não deve ser o mesmo operador que inicia a onda.

### 25.2 Pré-check do tenant e mailbox

```powershell
Import-Module ExchangeOnlineManagement
Connect-ExchangeOnline -UserPrincipalName <OPERADOR_APROVADO>

$upn = '<USUARIO_ALVO>'

$mailbox = Get-EXOMailbox -Identity $upn `
  -Properties ArchiveStatus,ArchiveGuid,ExchangeGuid,RecipientTypeDetails,
              RetentionHoldEnabled,LitigationHoldEnabled,AutoExpandingArchiveEnabled

$archiveStats = Get-EXOMailboxStatistics -Identity $upn -Archive -PropertySets All
$folderStats = Get-EXOMailboxFolderStatistics -Identity $upn -Archive -IncludeOldestAndNewestItems

[pscustomobject]@{
  UserPrincipalName = $upn
  ExchangeGuid = $mailbox.ExchangeGuid
  ArchiveGuid = $mailbox.ArchiveGuid
  ArchiveStatus = $mailbox.ArchiveStatus
  RecipientTypeDetails = $mailbox.RecipientTypeDetails
  AutoExpandingArchiveEnabled = $mailbox.AutoExpandingArchiveEnabled
  RetentionHoldEnabled = $mailbox.RetentionHoldEnabled
  LitigationHoldEnabled = $mailbox.LitigationHoldEnabled
  ArchiveItemCount = $archiveStats.ItemCount
  ArchiveTotalItemSize = $archiveStats.TotalItemSize
} | ConvertTo-Json -Depth 5

$folderStats |
  Select-Object Name,FolderPath,FolderType,ItemsInFolderAndSubfolders,
                FolderAndSubfolderSize,OldestItemReceivedDate,NewestItemReceivedDate |
  Export-Csv -LiteralPath '.\archive-folders-before.csv' -NoTypeInformation -Encoding UTF8

Disconnect-ExchangeOnline -Confirm:$false
```

O script de produção usa app-only/managed identity para leitura sempre que suportado e um role restrito. Mudanças como habilitar archive ou auto-expansion são tarefas separadas, aprovadas e nunca executadas implicitamente pelo precheck.

### 25.3 Habilitar archive somente com autorização

```powershell
Connect-ExchangeOnline -UserPrincipalName <ADMIN_DE_RECIPIENTES>

# Executar apenas após ticket/aprovação e licença confirmada.
Enable-Mailbox -Identity '<USUARIO_ALVO>' -Archive

Get-Mailbox -Identity '<USUARIO_ALVO>' |
  Format-List ArchiveStatus,ArchiveGuid,ArchiveName

Disconnect-ExchangeOnline -Confirm:$false
```

Auto-expansion não é habilitado pelo migrador para “abrir espaço”. Se o cliente decidir habilitá-lo para a política normal da mailbox, a operação é externa ao job e deve registrar que não pode ser desfeita conforme a documentação atual.

### 25.4 Capacity gate

O cálculo usa bytes planejados, tamanho atual observado, limite publicamente suportado e margem de segurança. Valores formatados retornados pelo PowerShell não devem ser parseados por regex em língua local; usar propriedades estruturadas/bytes quando disponíveis ou coleta controlada.

```text
if IsArchive:
  require ArchiveStatus == Active
  require plannedPstFiles.all(size <= policy.hardPartBytes)
  require wave.csvRowCount <= 500
  require targetRoot != "/"
  require plannedArchiveImportBytes <= provider.mainArchiveImportLimitBytes
  require plannedArchiveImportBytes <= observedAvailableMainArchiveBytes - safetyMargin
else:
  require plannedBytes <= observedPrimaryAvailableBytes - safetyMargin
```

> [!CAUTION]
> **BLOQUEIO / DECISÃO CRÍTICA**
> `AutoExpandingArchiveEnabled=True` não aumenta `provider.mainArchiveImportLimitBytes` do adapter Purview. Acima de 100 GB para o mesmo archive, o estado é `MICROSOFT_ASSESSMENT_REQUIRED`.

### 25.5 Coleta segura do SAS

No portal, o operador cria um novo import job, seleciona upload e copia a URL SAS. O produto oferece um formulário secreto:

- campo não ecoado e nunca incluído em analytics;
- valida host, esquema HTTPS, container `ingestiondata`, expiry e permissões esperadas;
- armazena temporariamente no Key Vault com expiração e tags de wave;
- nenhum log registra query string;
- somente a managed identity do upload worker lê o secret;
- secret é eliminado ou desabilitado após upload e janela de investigação;
- o processo AzCopy ocorre em worker efêmero dedicado.

O sistema não solicita que o operador coloque a URL em Word, e-mail, ticket ou linha de comando manual. O runbook oficial trata a URL como senha; o produto melhora esse controle.

### 25.6 Uso do AzCopy

O Purview exige a versão de AzCopy indicada/downloadada no próprio fluxo. A imagem do upload worker conserva binário e SHA-256 homologados. Atualização de versão exige compatibility test.

```powershell
param(
  [Parameter(Mandatory)] [string] $SourceDirectory,
  [Parameter(Mandatory)] [string] $PurviewSasUrl,
  [Parameter(Mandatory)] [string] $LogDirectory
)

$ErrorActionPreference = 'Stop'
$env:AZCOPY_LOG_LOCATION = $LogDirectory
$env:AZCOPY_JOB_PLAN_LOCATION = $LogDirectory
$env:AZCOPY_LOG_LEVEL = 'INFO'

$azcopy = 'C:\Program Files\PstMigration\AzCopy\azcopy.exe'
if (-not (Test-Path -LiteralPath $azcopy)) { throw 'Approved AzCopy not found.' }

# Não imprimir $PurviewSasUrl. O transcript desta sessão deve estar desabilitado.
& $azcopy copy $SourceDirectory $PurviewSasUrl --recursive=true
if ($LASTEXITCODE -ne 0) { throw "AzCopy failed with exit code $LASTEXITCODE" }
```

Na implementação .NET, usar `ProcessStartInfo.ArgumentList`, nunca concatenar uma string de shell. O sistema redige qualquer URL com query em exceções e telemetria. Como o SAS inevitavelmente aparece no command line do processo AzCopy, o worker deve ser dedicado, sem usuários interativos, com acesso administrativo JIT e lifetime curto.

### 25.7 Estrutura de upload

```text
ingestiondata/
  prj01-w001/
    p_01JABC_part001.pst
    p_01JABC_part002.pst
    p_01JDEF_part001.pst
```

`FilePath` no CSV será `prj01-w001`, sem `ingestiondata`, respeitando case. Nome de PST é único no job. A plataforma nunca reutiliza a mesma pasta para projetos diferentes.

### 25.8 CSV mapping oficial

Cabeçalho fixo:

```csv
Workload,FilePath,Name,Mailbox,IsArchive,TargetRootFolder,ContentCodePage,SPFileContainer,SPManifestContainer,SPSiteUrl
Exchange,prj01-w001,p_01JABC_part001.pst,user01@contoso.com,TRUE,/ImportedPst_PRJ01_W001,,,,
Exchange,prj01-w001,p_01JABC_part002.pst,user01@contoso.com,TRUE,/ImportedPst_PRJ01_W001,,,,
```

Validações do builder:

- exatamente dez colunas e cabeçalho idêntico;
- UTF-8 com BOM somente se compatibility test provar necessidade; default UTF-8 consistente;
- `Workload=Exchange`;
- `FilePath` case-sensitive e sem `ingestiondata`;
- `Name` case-sensitive, `.pst`, único, sanitizado e presente no manifest de upload;
- `Mailbox` resolvido por GUID/UPN correto; inactive mailbox usa GUID conforme documentação;
- `IsArchive=TRUE` somente após precheck;
- `TargetRootFolder=/ImportedPst_<Project>_<Wave>`; nunca `/`;
- campos SharePoint vazios;
- ≤ 500 data rows;
- SHA-256 do CSV incluído na evidência.

### 25.9 Passo a passo no portal Purview

64. Acessar `https://purview.microsoft.com` com conta elegível via PIM.
65. Abrir **Data Lifecycle Management → Microsoft 365 → Import**.
66. Selecionar **New import job**.
67. Informar o nome gerado pelo produto, apenas minúsculas, números, hífen e underscore.
68. Selecionar **Upload your data**.
69. Copiar o SAS para o formulário secreto da plataforma e obter o AzCopy suportado.
70. Após o produto indicar `UPLOAD_VERIFIED`, marcar que o upload terminou e que o mapping está disponível.
71. Selecionar o mapping CSV gerado.
72. Clicar **Validate** e anexar o validation report ao job da plataforma.
73. Se houver erro, não editar CSV manualmente; importar o report, corrigir dados e gerar nova versão.
74. Ler termos aplicáveis e salvar o job.
75. Registrar na plataforma o nome/ID do Purview job, operador, horário e screenshot/relatório.
76. Aguardar `Analysis completed`.
77. Abrir **Import to Microsoft 365**.
78. Aplicar filtro somente se ele corresponder à policy version aprovada; caso contrário, cancelar e voltar ao aprovador.
79. Iniciar **Import data**.
80. Acompanhar status por arquivo e anexar resultados.
81. Somente após conclusão iniciar reconciliação; `Complete` não fecha o projeto.

> [!NOTE]
> **CONTROLE DE SEGURANÇA**
> O operador não pode substituir o CSV pelo Excel “só para corrigir uma linha”. Toda mudança deve sair do generator, receber nova versão e novo hash. Isso preserva cadeia de custódia e evita case/encoding invisível.

### 25.10 Retenção do staging Microsoft

A documentação indica que os PSTs enviados ao container Microsoft são removidos automaticamente quando não há jobs em progresso, aproximadamente 30 dias após o job mais recente, e que o operador não consegue apagá-los. O produto registra essa limitação no data processing record e não promete deleção imediata desse staging.

## 26. Reconciliador do Purview

### 26.1 Fontes de evidência

- manifestos de origem e part;
- AzCopy result, plan/log sanitizado e file list;
- mapping CSV e hash;
- validation report do Purview;
- status por PST no portal;
- imported size, imported count e skipped/corrupted quando fornecidos;
- EXO mailbox e folder statistics antes/depois;
- amostra/consulta de conteúdo quando legalmente autorizada;
- aprovações e dispositions.

### 26.2 Estatísticas pós-import

```powershell
Connect-ExchangeOnline -UserPrincipalName <OPERADOR_LEITURA>

$upn = '<USUARIO_ALVO>'
Get-EXOMailboxStatistics -Identity $upn -Archive -PropertySets All |
  Select-Object DisplayName,ItemCount,TotalItemSize,TotalDeletedItemSize,LastLogonTime |
  ConvertTo-Json -Depth 5 |
  Set-Content -LiteralPath '.\archive-statistics-after.json' -Encoding UTF8

Get-EXOMailboxFolderStatistics -Identity $upn -Archive -IncludeOldestAndNewestItems |
  Select-Object Name,FolderPath,FolderType,ItemsInFolder,ItemsInFolderAndSubfolders,
                FolderSize,FolderAndSubfolderSize,OldestItemReceivedDate,NewestItemReceivedDate |
  Export-Csv -LiteralPath '.\archive-folders-after.csv' -NoTypeInformation -Encoding UTF8

Disconnect-ExchangeOnline -Confirm:$false
```

`TargetRootFolder` exclusivo por project/wave permite localizar e medir o import. Os cmdlets podem incluir itens ocultos em algumas métricas; o reconciliador documenta cada propriedade em vez de comparar números incompatíveis.

### 26.3 Resultado de reconciliação

| **Resultado** | **Condição** | **Estado** |
| --- | --- | --- |
| PASS | esperado = importado, nenhum erro material, evidência completa | pronto para aprovação |
| PASS\_WITH\_EXPLAINED\_EXCEPTIONS | diferenças têm item IDs/classes e disposition aprovada | pronto, com ressalvas |
| INCONCLUSIVE | serviço não expõe granularidade suficiente | requer amostra/eDiscovery ou suporte |
| FAIL | item/bytes/pastas faltantes sem explicação | quarentena/incidente |
| DUPLICATE\_RISK | target/root/hash divergiu de execução anterior | bloqueado |

### 26.4 Retention Hold

Não presumir que o serviço deixou a mailbox em um estado específico. O precheck e postcheck coletam `RetentionHoldEnabled`, holds e políticas. O sistema jamais remove hold automaticamente. Alteração exige owner de compliance, ticket, before/after e confirmação de que itens importados não serão excluídos ou movidos contra a intenção.

## 27. Cenários acima de 100 GB no mesmo archive

O Purview adapter retorna bloqueio. O operador recebe um pacote para suporte Microsoft com:

- tenant e região;
- ExchangeGuid/ArchiveGuid;
- licenciamento e archive status;
- tamanho atual e planejado;
- quantidade de PSTs/parts e maior part;
- política de target root;
- business/legal justification;
- documentação oficial usada no bloqueio;
- pergunta objetiva sobre o caminho suportado;
- prazo e impacto.

O job fica `WAITING_EXTERNAL`. Um parecer Microsoft pode autorizar uma operação específica; essa autorização é armazenada como capability evidence com tenant/scope/expiry. Não transformar um parecer pontual em capacidade global.

## 28. Adapter Graph Mailbox Import/Export - trilha estratégica bloqueada

### 28.1 O que a API oferece

- recursos sob `/v1.0/admin/exchange/mailboxes/{mailboxId}`;
- export de até 20 itens por request em stream opaco de alta fidelidade;
- criação de import session;
- import URL preauthenticated e expirável no domínio Outlook;
- upload por item com `FolderId`, `Mode` e `Data` FTS base64;
- permissões `MailboxItem.ImportExport` ou `MailboxItem.ImportExport.All`;
- documentação conceitual que menciona archives, mas páginas v1.0 recentes ainda listam primária/shared e páginas de redirect usam beta.

### 28.2 Por que não é o adapter GA do PST

- não recebe PST;
- o `Data` é FTS, não MIME/MSG/PST;
- o cenário documentado é reimportar dados exportados pela própria família de APIs;
- transformar itens PST em FTS exige implementar/usar Fast Transfer completo e demonstrar suporte;
- archive discovery/redirect ainda precisa de homologação por versão;
- APIs recentes podem ter throttling/limites ainda não adequados a centenas de milhões de itens;
- consentimento application-wide é altamente privilegiado.

### 28.3 Gate de habilitação

Todos os itens abaixo precisam estar verdadeiros:

82. API v1.0 GA para o cenário e cloud do cliente.
83. Descoberta do main/auxiliary archive documentada e testada.
84. Microsoft confirma que FTS produzido a partir do PST/EV é suportado.
85. SDK/codec FTS passa fidelity corpus e análise de segurança.
86. Throttling e custo são conhecidos.
87. Application Access Policy ou mecanismo equivalente restringe mailboxes.
88. Consentimento aprovado pelo cliente.
89. Redirect 308, `ErrorArchiveFolderMovedPermanently` e 409 de import session implementados.
90. Pen-test e load test concluídos.
91. Capability evidence aprovada pelo Change Advisory Board.

### 28.4 Esqueleto do adapter bloqueado

```csharp
public sealed class GraphFtsTargetIngestor : ITargetIngestor
{
    private readonly ICapabilityGate _gate;

    public TargetProvider Provider => TargetProvider.GraphMailboxFts;

    public async Task<TargetPrecheckResult> PrecheckAsync(
        ImportWave wave,
        CancellationToken cancellationToken)
    {
        var capability = await _gate.GetAsync(
            wave.TenantId,
            CapabilityNames.GraphFtsArchiveImport,
            cancellationToken);

        if (!capability.IsApprovedAndCurrent)
            return TargetPrecheckResult.Blocked(
                "GRAPH_FTS_ARCHIVE_NOT_APPROVED",
                "Graph FTS archive import is disabled until its support gate is approved.");

        // O restante só existe após o gate e os testes contratuais.
        return TargetPrecheckResult.Ready(capability.EvidenceId);
    }
}
```

## 29. Adapter contratual de ingestão rápida

Se a empresa obtiver acesso a protocolo/SDK de partner, ele entra em projeto separado, com:

- contrato de suporte e redistribuição;
- versão e hash do SDK;
- permissões e endpoints documentados;
- testes de fidelidade e escala;
- quotas e throttling;
- processo de revogação;
- plano de saída se o contrato terminar;
- feature flag por tenant.

O domínio não muda. `PreparedItem`, `DestinationRun` e `ReconciliationObservation` continuam os contratos. Essa é a razão para adapters desde o início.
