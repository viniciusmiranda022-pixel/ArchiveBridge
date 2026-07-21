# EV Capability Discovery — especificação

Etapa **obrigatória** que precede a seleção de qualquer adapter
([ADR-0013](../adr/0013-exportacao-ev-multiversao.md)). Executa no host do
Source Connector (outbound-only, §15 do runbook), coleta capacidades
observadas — nunca presumidas pelo número de versão — e produz um
**relatório de capacidades assinável**, persistido como evidência do
projeto.

## O que o discovery coleta

| Grupo | Itens |
| --- | --- |
| Instalação | versão e build instalados do Enterprise Vault (registro/DLLs), edição, idioma; Directory/Storage service acessíveis |
| Cmdlets | presença e origem do snap-in/módulo (`Symantec.EnterpriseVault.PowerShell.Snapin` ou equivalente); disponibilidade real de `Get-EVArchive`, `Export-EVArchive` e cmdlets auxiliares (invocação de sondagem sem efeito colateral) |
| Descoberta de archives | suporte a inventário programático (`Get-EVArchive`), campos retornados (ArchiveId, Type, VaultStore, Status), paginação/escala |
| Exportação PowerShell | suporte a `Export-EVArchive`; parâmetros **efetivamente aceitos pelo build** (`-MaxPSTSizeMB` — intervalo detectado, não presumido —, `-Format PST`, `-MaxThreads`); export Unicode |
| Segmentação | segmentação nativa por tamanho: **detectar** `DetectedMinPstSizeMb`/`DetectedMaxPstSizeMb` do ambiente e o comportamento no limite (ver "Política de tamanho de PST" abaixo) |
| Retry | comportamento de reexecução do exporter (retomada, sobrescrita, sufixos de parte) |
| Relatório de exportação | existência, caminho e formato do relatório do exporter (ExportReport/transcript), campos disponíveis por versão |
| Pré-requisitos | Outlook instalado quando exigido pela família (bitness compatível), permissões da conta de serviço no Vault Store/archives, espaço em disco de staging, exceções do exporter (§16.4) |

## Contrato de saída

```json
{
  "discoveryVersion": "1.0",
  "collectedAtUtc": "<timestamp>",
  "host": "<opaco>",
  "evVersion": { "display": "14.2.2", "build": "<build>", "candidateFamily": "12.1-15.x" },
  "capabilities": {
    "archiveDiscovery":   { "state": "SUPPORTED|UNSUPPORTED|UNKNOWN", "evidence": "..." },
    "powershellExport":   { "state": "...", "evidence": "..." },
    "unicodeExport":      { "state": "...", "evidence": "..." },
    "sizeSegmentation":   {
      "state": "...",
      "detectedMinPstSizeMb": 0,
      "detectedMaxPstSizeMb": 0,
      "evidence": "intervalo medido no build; não presumido"
    },
    "nativeRetry":        { "state": "...", "evidence": "..." },
    "exportReport":       { "state": "...", "format": "csv|xml|text|unknown" }
  },
  "sizePolicy": {
    "archiveBridgeOperationalTargetMb": 18432,
    "microsoftHardPolicyBytes": 21474836480,
    "targetWithinDetectedRange": false
  },
  "prerequisites": {
    "outlook":            { "state": "PRESENT|ABSENT|NOT_REQUIRED", "version": "..." },
    "serviceAccountPerms":{ "state": "VERIFIED|INSUFFICIENT|UNKNOWN", "detail": "..." },
    "stagingDiskFreeGb":  0
  },
  "reportSha256": "<hash do relatório completo>"
}
```

## Política de tamanho de PST

O tamanho de segmento é sempre a **política do ArchiveBridge validada
contra o que o ambiente realmente aceita** — nunca um valor presumido:

| Termo | Origem | Valor |
| --- | --- | --- |
| `DetectedMinPstSizeMb` / `DetectedMaxPstSizeMb` | **detectado** no build do EV | varia por ambiente |
| `ArchiveBridgeOperationalTargetMb` | política do ArchiveBridge (margem) | `18432` |
| `MicrosoftHardPolicyBytes` | limite duro do destino M365 (referência) | `20480 MB` (20 GB) |

O discovery marca `targetWithinDetectedRange = true` somente se
`DetectedMinPstSizeMb ≤ 18432 ≤ DetectedMaxPstSizeMb`. Caso contrário, a
configuração é **rejeitada** (`EV_PREREQ_FAILED`): ajusta-se o alvo dentro
do intervalo detectado, respeitando `MicrosoftHardPolicyBytes`, ou o
ambiente segue em modo assistido/bloqueio. `18432` **não** é um default
nativo do EV; é o alvo do produto, escolhido com margem abaixo do limite
do destino.

## Regras

1. **`UNKNOWN` não é `SUPPORTED`** — capacidade não confirmada conta como
   ausente na seleção de adapter (fail closed).
2. Sondagens **não podem ter efeito colateral**: nenhuma exportação real,
   nenhuma escrita no Vault Store; apenas leitura/metadados.
3. O relatório de capacidades é **evidência**: hash SHA-256, vínculo com
   projeto/host e retenção junto às demais evidências do runbook.
4. A seleção de adapter consome **exclusivamente** o relatório de
   discovery — nunca a string de versão isolada.
5. Mudança de build/patch no host invalida o relatório anterior e exige
   novo discovery antes da próxima onda.

## Seleção do adapter a partir do discovery

| Condição observada | Adapter |
| --- | --- |
| Família candidata (12.1–15.x) **e** `powershellExport`, `unicodeExport`, `sizeSegmentation` = SUPPORTED **e** `targetWithinDetectedRange` **e** build certificado | **EV PowerShell Adapter** |
| Família 10.x/11.x/12.0 com adapter legado **certificado** para o build | **EV Legacy Script Adapter** correspondente |
| Qualquer outro caso | **Assisted Export Adapter** (modo assistido) ou bloqueio controlado, conforme política do projeto |

A "família candidata" apenas **habilita a avaliação** do adapter PowerShell
nativo; a seleção efetiva exige as capabilities obrigatórias, o alvo de
tamanho dentro do intervalo detectado e a certificação do build. Nenhuma
versão é suportada pela string de versão isolada.

Erros de seleção (família desconhecida, relatório inválido, capability
UNKNOWN em item obrigatório) falham de forma controlada com o código
`EV_ADAPTER_UNRESOLVED` — nunca com fallback silencioso.
