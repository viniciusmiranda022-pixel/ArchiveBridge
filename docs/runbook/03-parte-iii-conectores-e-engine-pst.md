<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

# Parte III - Conectores de origem e engine PST

## 15. Conector de origem: desenho seguro

O Source Connector é instalado perto do dado. Ele inicia conexões outbound, baixa uma política assinada, executa apenas ações allowlisted e publica metadados/resultados. O plano de controle não recebe SMB nem credenciais do EV.

### 15.1 Registro do conector

21. Operador cria um enrollment token de uso único, validade máxima de 15 minutos.
22. Instalação gera chave privada não exportável no Windows Certificate Store ou TPM.
23. Conector troca o token por certificado de cliente curto e identidade própria.
24. Plano de controle registra thumbprint, hostname, versão, site e capabilities.
25. Toda chamada usa mTLS, token de workload e assinatura do payload.
26. Certificados renovam automaticamente; revogação bloqueia imediatamente novos jobs.

### 15.2 Diretórios locais

```text
D:\PstMigration\
  landing\<projectId>\<sourceId>\       # somente ingest, ACL do serviço
  work\<jobId>\<attemptId>\             # temporário, apagável após aprovação
  output\<jobId>\<partId>\              # parte antes do upload privado
  quarantine\<projectId>\<artifactId>\  # acesso JIT, retenção longa
  evidence-cache\<jobId>\                # cache transitório assinado
  logs\                                    # sem PII/SAS
```

O serviço recusa symlink, junction, mount point, alternate data stream, caminho UNC não allowlisted e qualquer path que escape da raiz normalizada. A cópia para landing usa arquivo temporário, `FlushFileBuffers`, tamanho, hash, rename atômico e somente então publica `SourceLanded`.

## 16. Inventário e exportação do Enterprise Vault

### 16.1 Pré-requisitos do host EV

- versão do Enterprise Vault e PowerShell snap-in suportadas pelo ambiente;
- conta de serviço dedicada com o menor papel EV necessário;
- Outlook presente e definido como cliente padrão somente porque `Export-EVArchive -Format PST` assim exige no servidor local e no Storage service pertinente;
- volume de saída com espaço livre de pelo menos `1,3 × dados exportáveis da janela`;
- antivírus com exclusões estritamente documentadas para arquivos em gravação e scan após fechamento;
- janela e `MaxThreads` definidos após benchmark para não degradar o EV;
- relógio sincronizado, transcript PowerShell protegido e logs enviados à plataforma.

> [!WARNING]
> **ATENÇÃO**
> Outlook é uma dependência do exporter oficial do EV, não a engine do produto. Não automatizar Outlook/COM. Se o cliente não aceitar Outlook no host necessário, exportar em formato NATIVE/EML e usar um adapter aprovado, ou usar ferramenta/fabricante autorizado.

### 16.2 Carregar o snap-in e inventariar

```powershell
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Get-PSSnapin -Registered | Where-Object Name -eq 'Symantec.EnterpriseVault.PowerShell.Snapin')) {
    throw 'Enterprise Vault PowerShell snap-in is not registered on this host.'
}

if (-not (Get-PSSnapin | Where-Object Name -eq 'Symantec.EnterpriseVault.PowerShell.Snapin')) {
    Add-PSSnapin Symantec.EnterpriseVault.PowerShell.Snapin
}

$inventoryPath = 'D:\PstMigration\evidence-cache\ev-archives.csv'
Get-EVArchive |
    Select-Object ArchiveName, ArchiveId, ArchiveType, VaultStoreName, Status |
    Sort-Object ArchiveName |
    Export-Csv -LiteralPath $inventoryPath -NoTypeInformation -Encoding UTF8

Get-FileHash -Algorithm SHA256 -LiteralPath $inventoryPath
```

Os nomes de propriedades podem variar por versão do EV; o adapter possui uma support matrix e um teste de descoberta que falha antes de exportar se o schema real divergir. Não alterar o script “na hora” em produção.

### 16.3 Exportar em partes de 18 GiB

O cmdlet documenta `-MaxPSTSizeMB` entre 500 e 51200 MB. O default do produto será 18432 MB para deixar margem abaixo da recomendação Microsoft de 20 GB.

```powershell
param(
    [Parameter(Mandatory)] [string] $ArchiveId,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [ValidateRange(1,32)] [int] $MaxThreads = 8,
    [ValidateRange(500,51200)] [int] $MaxPstSizeMb = 18432
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Start-Transcript -LiteralPath (Join-Path $OutputDirectory 'export.transcript.txt')
try {
    Export-EVArchive `
        -ArchiveId $ArchiveId `
        -OutputDirectory $OutputDirectory `
        -Format PST `
        -MaxThreads $MaxThreads `
        -MaxPSTSizeMB $MaxPstSizeMb `
        -Retry

    Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.pst' -File |
        ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name
                Length = $_.Length
                Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            }
        } | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'export-manifest.csv') -NoTypeInformation -Encoding UTF8
}
finally {
    Stop-Transcript
}
```

### 16.4 Exceções do exporter EV

Segundo a documentação do cmdlet, item acima de 250 MB pode ficar fora do PST; acima de 2 GB pode ser exportado no formato nativo. O conector deve varrer arquivos externos, registrar seu vínculo e direcioná-los à fila `OversizedItem`. O certificado final não pode omitir esses itens.

### 16.5 Incremental e freeze

Para retirada do EV sem perda de mudanças:

27. executar baseline por archive;
28. registrar watermark de consulta/data/ID conforme capacidade oficial da versão EV;
29. manter acesso do usuário ao legado durante baseline;
30. executar delta com filtro aprovado;
31. congelar ingestão/shortcut somente na janela autorizada;
32. executar delta final e reconciliar;
33. trocar acesso do usuário após critério de conclusão;
34. preservar EV pelo período de rollback contratual;
35. descomissionar apenas com sign-off e retenção satisfeita.

O detalhe de delta depende das APIs e da versão do EV. Não usar apenas `ReceivedDate` sem avaliar backdating, itens alterados e calendários. O adapter mantém strategy específica por versão.

## 17. Ingestão de PST já existente

### 17.1 Sequência

36. resolver owner e target antes da cópia;
37. obter tamanho e atributos sem confiar no nome;
38. copiar para landing com arquivo temporário;
39. calcular SHA-256 durante ou imediatamente após cópia;
40. confirmar que tamanho permanece estável por duas leituras;
41. bloquear escrita e registrar ACL;
42. detectar ANSI/Unicode, senha e possibilidade de abertura;
43. criar inspection job;
44. preservar o original mesmo se reparo for necessário;
45. qualquer reparo gera novo artifact derivado.

### 17.2 Hash streaming em C#

```csharp
public static async Task<string> ComputeSha256Async(
    string path,
    CancellationToken cancellationToken)
{
    const int BufferSize = 4 * 1024 * 1024;
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    using var sha = System.Security.Cryptography.SHA256.Create();
    var hash = await sha.ComputeHashAsync(stream, cancellationToken);
    return Convert.ToHexString(hash).ToLowerInvariant();
}
```

### 17.3 Verificação de estabilidade

```csharp
public static async Task EnsureStableFileAsync(
    string path,
    TimeSpan observationWindow,
    CancellationToken cancellationToken)
{
    var first = new FileInfo(path);
    var length = first.Length;
    var write = first.LastWriteTimeUtc;
    await Task.Delay(observationWindow, cancellationToken);
    first.Refresh();

    if (first.Length != length || first.LastWriteTimeUtc != write)
        throw new SourceStillChangingException(path);
}
```

## 18. Seleção da engine PST

O produto não implementará \[MS-PST\] do zero. A especificação aberta é referência para investigação e verificação, não justificativa para escrever um parser/writer completo, com anos de edge cases, antes de entregar valor.

### 18.1 Decisão recomendada

- **Engine primária:** Aspose.Email for .NET com licença OEM/deployment adequada. Usada para abrir, enumerar, criar e dividir PSTs.
- **Validador independente:** libpff/pffinfo/pffexport em container/worker isolado, exclusivamente leitura e verificação. Seu status e licença precisam de avaliação jurídica.
- **Fallback de reparo:** ScanPST em estação/worker Windows controlado, sempre sobre cópia derivada, nunca sobre original.
- **Outlook/COM:** proibido no engine central.

### 18.2 Interface do adapter

```csharp
public interface IPstEngine
{
    string EngineName { get; }
    string EngineVersion { get; }

    Task<PstInspectionResult> InspectAsync(
        PstArtifact artifact,
        InspectionPolicy policy,
        CancellationToken cancellationToken);

    IAsyncEnumerable<PstPartCreated> PartitionAsync(
        PstArtifact artifact,
        PartitionPlan plan,
        CancellationToken cancellationToken);

    Task<PstValidationResult> ValidateAsync(
        PstArtifact artifact,
        ValidationPolicy policy,
        CancellationToken cancellationToken);
}
```

O domínio recebe resultados normalizados. Tipos de `Aspose.Email` ou libpff nunca atravessam a interface.

## 19. Inspeção estrutural

O inspector coleta sem extrair corpos/anexos para disco:

- formato ANSI/Unicode e versão;
- tamanho físico e hash;
- árvore de pastas com path normalizado e entry ID protegido;
- contagem de itens por pasta e total;
- bytes lógicos estimados;
- datas mínima/máxima por pasta;
- classes MAPI encontradas;
- pastas padrão, hidden/non-IPM e soft-deleted quando acessível;
- itens sem data, muito grandes, corrompidos ou não enumeráveis;
- profundidade e comprimento de path;
- duplicidades potenciais por fingerprint;
- senha/criptografia e capacidade de abertura;
- tempo, pico de memória, throughput e versão da engine.

### 19.1 Risk score

| **Sinal** | **Peso sugerido** |
| --- | --- |
| não abre na engine primária | bloqueio |
| hash muda durante leitura | bloqueio |
| erro estrutural/CRC | +40 |
| item não enumerável | +20 por classe, teto 60 |
| PST \> 100 GB | +20 |
| PST \> 500 GB | +35 |
| pasta \> 100.000 itens | +15 |
| path inválido/conflitante | +15 |
| formato ANSI | +10 |
| data ausente ou fora do intervalo plausível | +5 |

`0–19` baixo; `20–49` moderado; `50–79` alto; `80+` crítico. O score não decide sozinho: bloqueios objetivos têm precedência.

## 20. Planejamento e particionamento

### 20.1 Política default

- `TargetPartBytes = 18 GiB`.
- `HardPartBytes = 20 GB`, considerando a convenção documentada pelo destino.
- preservar pasta inteira quando couber e não violar contagem/risco;
- pasta grande é particionada por data; sem distribuição útil, usar bin packing estável;
- ordem estável por `folderPathNormalized + receivedUtc + stableItemFingerprint`;
- nomes não incluem UPN completo; usar IDs opacos;
- cada part gera manifesto antes de validação;
- nenhum part é reaproveitado entre tenants ou targets.

### 20.2 Identidade determinística do plano

```text
planHash = SHA256(
  sourceSha256 +
  canonicalJson(partitionPolicy) +
  pstEngineName +
  pstEngineVersion +
  plannerVersion
)
```

Dois plans com inputs iguais devem gerar a mesma associação lógica de itens. Se a biblioteca produzir bytes diferentes, cada saída recebe hash próprio; apenas um conjunto pode ser aprovado.

### 20.3 Split simples por tamanho com Aspose

```csharp
using Aspose.Email.Storage.Pst;

public sealed class AsposePstSplitter
{
    private const long TargetChunkBytes = 18L * 1024 * 1024 * 1024;

    public Task SplitAsync(
        string sourcePath,
        string outputDirectory,
        string safePrefix,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pst = PersonalStorage.FromFile(sourcePath, writable: false);
            pst.StorageProcessing += (_, e) =>
            {
                // Publicar somente nome sanitizado/progresso; nunca assunto ou SAS.
                cancellationToken.ThrowIfCancellationRequested();
            };
            pst.SplitInto(TargetChunkBytes, safePrefix, outputDirectory);
        }, cancellationToken);
    }
}
```

O `SplitInto` cria partes de tamanho aproximado. Depois, o sistema calcula hash e inspeciona cada saída. Qualquer parte acima do hard limit volta ao planner; não é enviada assim mesmo.

### 20.4 Partição semântica

Para preservar melhor pastas e permitir reconciliação detalhada, o planner avançado cria novos PSTs Unicode, replica a árvore necessária e copia mensagens conforme um plano de item IDs. A implementação precisa:

46. inventariar sem carregar todos os itens em memória;
47. gravar assignment em arquivo/banco temporário ordenado;
48. abrir um único part writer por vez;
49. copiar item e propriedades no formato nativo da biblioteca;
50. fechar/flush do PST;
51. reabrir e reinspecionar;
52. comparar item count/fingerprints;
53. publicar evento `PartCreated` apenas após sucesso.

Nunca lançar todos os itens como tasks paralelas. PST writer é recurso serial; paralelismo ocorre entre arquivos/fontes independentes com limite de IOPS.

### 20.5 Regra após início da importação

Depois que uma part é usada em qualquer import:

- o artifact passa a `IMPORT_LOCKED`;
- bytes não podem ser apagados antes do fim da janela de rollback;
- replay usa exatamente o mesmo hash e target root;
- se a part for perdida, o sistema não a regenera automaticamente;
- reprocessamento a partir da origem cria nova lineage e exige análise de duplicidade;
- overlapping content em PST diferente não é deduplicado por conteúdo pelo Purview.

> [!CAUTION]
> **BLOQUEIO / DECISÃO CRÍTICA**
> A deduplicação Purview depende de `SourceEntryId` e do mesmo target folder. Regenerar uma parte pode produzir novos EntryIds mesmo que o conteúdo pareça igual. Por isso, “recriar o PST e reenviar” é bloqueado depois do primeiro import.

## 21. Fingerprint e cadeia de custódia por item

O fingerprint do produto serve para reconciliação e detecção de overlap; ele não substitui o SourceEntryId do serviço Microsoft.

### 21.1 Canonical item fingerprint

```text
SHA256(
  messageClassNormalized || 0x1F ||
  internetMessageIdNormalized || 0x1F ||
  sentOrReceivedUtcTicks || 0x1F ||
  senderNormalized || 0x1F ||
  recipientsCanonicalHash || 0x1F ||
  subjectCanonicalHash || 0x1F ||
  bodyCanonicalHash || 0x1F ||
  attachmentManifestHash || 0x1F ||
  logicalSize
)
```

Para reduzir exposição, hashes de campos podem ser HMAC-SHA256 com chave por tenant. Não guardar assunto, corpo ou endereço em claro no banco de controle. O manifesto detalhado vive criptografado no storage de evidência, acessível por auditor JIT.

### 21.2 Custody event encadeado

```json
{
  "sequence": 1821,
  "tenantId": "tnt_01J...",
  "artifactId": "part_01J...",
  "eventType": "PstPartValidated",
  "occurredAt": "2026-07-20T13:10:22.319Z",
  "actor": "workload:pst-validator",
  "previousEventHash": "6ad0...",
  "payloadHash": "9bb2...",
  "eventHash": "sha256(canonical-event-without-eventHash)",
  "correlationId": "..."
}
```

O hash encadeado torna alteração detectável; Blob WORM impede alteração dentro da retenção; assinatura do pacote final permite verificação externa.

## 22. Corrupção, reparo e quarentena

### 22.1 Fluxo

54. abrir original em modo read-only;
55. registrar erro exato, offset/entry quando disponível e engine version;
56. tentar enumeração tolerante aprovada;
57. se política permitir, criar cópia derivada para reparo;
58. executar ScanPST em worker Windows isolado, sem acesso ao destino;
59. preservar `.bak`, transcript e exit/result;
60. calcular novo hash da cópia reparada;
61. validar com engine primária e independente;
62. comparar inventários pré/pós;
63. aprovar derivado ou mover para quarentena.

### 22.2 Proibições

- nunca executar ScanPST sobre o original;
- nunca chamar UI interativa em serviço sem wrapper controlado;
- nunca interpretar “abriu” como integridade;
- nunca ocultar itens perdidos ou movidos para Lost and Found;
- nunca marcar part como válida se os contadores não fecharem sem disposition;
- nunca excluir `.bak` antes do prazo de retenção definido.

### 22.3 Motivos de quarentena

| **Código** | **Significado** | **Owner** |
| --- | --- | --- |
| `PST_UNREADABLE` | nenhuma engine abre | especialista PST |
| `PST_PASSWORD_UNKNOWN` | senha necessária e não fornecida | cliente/segurança |
| `PST_HASH_CHANGED` | fonte alterada durante ingestão | source owner |
| `PST_STRUCTURE_DIVERGENCE` | engines divergem além da tolerância | engenharia |
| `PART_OVERSIZE` | saída acima do hard limit | planner |
| `ITEM_UNEXPORTABLE` | item individual não exportável | especialista |
| `MALWARE_DETECTED` | artefato/anexo detectado pela política | segurança |
| `TARGET_IDENTITY_AMBIGUOUS` | owner/target não confirmado | migration manager |

## 23. Validação independente

A validação deve combinar:

- SHA-256 do arquivo inteiro;
- abertura e enumeração com engine primária;
- abertura e contagem com engine independente;
- contagem por folder path;
- distribuição por message class;
- datas mínima/máxima;
- soma de tamanho lógico dentro de tolerância documentada;
- amostra estratificada de fingerprints;
- verificação de Unicode e paths;
- ausência de part acima do limite;
- reabertura após fechamento do writer.

### 23.1 Tolerâncias

Diferenças de “tamanho lógico” entre engines podem ocorrer por representação. Contagem de itens elegíveis, hierarquia e fingerprints amostrados não podem divergir silenciosamente. Tolerância só existe para uma métrica após ADR e corpus de prova.

### 23.2 Definition of Done de uma part

- artifact em storage privado e hash conferido;
- manifesto schema-valid e canonical hash calculado;
- contagem esperada gravada;
- engine primária `PASS`;
- validator independente `PASS` ou exceção formal;
- malware scan conforme política;
- target mapping resolvido;
- custody events presentes;
- nenhum segredo/PII nos logs;
- state `VALIDATED` persistido por transação.
