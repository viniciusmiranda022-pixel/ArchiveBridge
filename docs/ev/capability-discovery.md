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
| Exportação PowerShell | suporte a `Export-EVArchive`; parâmetros aceitos (`-MaxPSTSizeMB` 500–51200, `-Format PST`, `-MaxThreads`); export Unicode |
| Segmentação | segmentação nativa por tamanho e comportamento no limite (§16.3: default do produto 18432 MB) |
| Retry | comportamento de reexecução do exporter (retomada, sobrescrita, sufixos de parte) |
| Relatório de exportação | existência, caminho e formato do relatório do exporter (ExportReport/transcript), campos disponíveis por versão |
| Pré-requisitos | Outlook instalado quando exigido pela família (bitness compatível), permissões da conta de serviço no Vault Store/archives, espaço em disco de staging, exceções do exporter (§16.4) |

## Contrato de saída

```json
{
  "discoveryVersion": "1.0",
  "collectedAtUtc": "<timestamp>",
  "host": "<opaco>",
  "evVersion": { "display": "14.2.2", "build": "<build>", "family": "12.1+" },
  "capabilities": {
    "archiveDiscovery":   { "state": "SUPPORTED|UNSUPPORTED|UNKNOWN", "evidence": "..." },
    "powershellExport":   { "state": "...", "evidence": "..." },
    "unicodeExport":      { "state": "...", "evidence": "..." },
    "sizeSegmentation":   { "state": "...", "maxPstSizeMbRange": [500, 51200] },
    "nativeRetry":        { "state": "...", "evidence": "..." },
    "exportReport":       { "state": "...", "format": "csv|xml|text|unknown" }
  },
  "prerequisites": {
    "outlook":            { "state": "PRESENT|ABSENT|NOT_REQUIRED", "version": "..." },
    "serviceAccountPerms":{ "state": "VERIFIED|INSUFFICIENT|UNKNOWN", "detail": "..." },
    "stagingDiskFreeGb":  0
  },
  "reportSha256": "<hash do relatório completo>"
}
```

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
| `powershellExport`, `unicodeExport`, `sizeSegmentation` = SUPPORTED e família certificada | **EV PowerShell Adapter** |
| Família 10.x/11.x/12.0 com adapter legado **certificado** para o build | **EV Legacy Script Adapter** correspondente |
| Qualquer outro caso | **Assisted Export Adapter** (modo assistido) ou bloqueio controlado, conforme política do projeto |

Erros de seleção (família desconhecida, relatório inválido, capability
UNKNOWN em item obrigatório) falham de forma controlada com o código
`EV_ADAPTER_UNRESOLVED` — nunca com fallback silencioso.
