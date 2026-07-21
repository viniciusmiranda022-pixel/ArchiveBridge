# Contrato comum dos adapters de exportação EV — `IEvExportAdapter`

Interface **estável** entre o produto e o Enterprise Vault
([ADR-0013](../adr/0013-exportacao-ev-multiversao.md)). Todos os adapters
(PowerShell nativo, legados por família, assistido) implementam o mesmo
contrato e passam pelos **mesmos testes de contrato**
([test-plan.md](test-plan.md)); o domínio nunca vê tipos específicos de
versão do EV.

## Interface (assinatura de referência)

```csharp
public interface IEvExportAdapter
{
    string AdapterId { get; }                 // ex.: "ev-ps@1", "ev-legacy-11x@2"
    EvAdapterKind Kind { get; }               // PowerShellNative | LegacyScript | Assisted

    // 1. Descoberta de archives
    Task<IReadOnlyList<EvArchiveDescriptor>> DiscoverArchivesAsync(
        EvCapabilityReport capabilities, CancellationToken ct);

    // 2. Validação de pré-requisitos (fail closed; sem efeito colateral)
    Task<EvPrecheckResult> ValidatePrerequisitesAsync(
        EvExportRequest request, CancellationToken ct);

    // 3. Início da exportação (idempotente por ExportRequestId)
    Task<EvExportHandle> StartExportAsync(
        EvExportRequest request, CancellationToken ct);

    // 4. Consulta de progresso
    Task<EvExportProgress> GetProgressAsync(
        EvExportHandle handle, CancellationToken ct);

    // 5. Cancelamento (melhor esforço; estado final consistente)
    Task CancelAsync(EvExportHandle handle, CancellationToken ct);

    // 6. Retry (retoma/reexecuta sem duplicar o conjunto aprovado)
    Task<EvExportHandle> RetryAsync(
        EvExportHandle previous, CancellationToken ct);

    // 7. Leitura do relatório de exportação (normalizado)
    Task<EvExportReport> ReadExportReportAsync(
        EvExportHandle handle, CancellationToken ct);

    // 8. Inventário dos PSTs produzidos (com SHA-256 e vínculo ao archive)
    Task<IReadOnlyList<EvProducedPst>> InventoryOutputAsync(
        EvExportHandle handle, CancellationToken ct);
}
```

## Semântica obrigatória

| Operação | Regras |
| --- | --- |
| Descoberta | resultado determinístico para o mesmo estado do EV; IDs opacos; nenhum dado de conteúdo |
| Pré-requisitos | valida permissões, disco, Outlook (quando a família exige), exceções do exporter (§16.4); qualquer item reprovado bloqueia com código específico |
| Exportação | **idempotente por `ExportRequestId`**: reinvocar com o mesmo ID não cria segunda exportação; saída segmentada em **PST Unicode** com tamanho-alvo = `ArchiveBridgeOperationalTargetMb` (política do produto, `18432`), **validado contra `[DetectedMinPstSizeMb, DetectedMaxPstSizeMb]`** do ambiente antes de exportar — ver [capability-discovery.md](capability-discovery.md); não é um default do EV |
| Progresso | monotônico; unidades normalizadas (itens e/ou bytes); nunca expõe assunto/corpo |
| Cancelamento | após cancelar, `GetProgress` converge para estado terminal `CANCELLED`; artefatos parciais ficam marcados, nunca aprovados |
| Retry | **não pode duplicar o conjunto aprovado**: partes já validadas/aprovadas são preservadas ou substituídas atomicamente, nunca somadas |
| Relatório | normalizado para `EvExportReport` (contagens, exceções §16.4, duração); o formato bruto por versão fica encapsulado no adapter |
| Inventário | cada PST com SHA-256, tamanho, sequência e **vínculo ao archive de origem** (ArchiveId + ExportRequestId) |

## Taxonomia de exceções (única para todos os adapters)

| Código | Significado | Ação do orquestrador |
| --- | --- | --- |
| `EV_ADAPTER_UNRESOLVED` | discovery não habilita adapter certificado | modo assistido ou bloqueio |
| `EV_PREREQ_FAILED` | pré-requisito reprovado (permissão, disco, Outlook) | bloquear e reportar item |
| `EV_ARCHIVE_NOT_FOUND` | archive inexistente/inacessível | falha controlada da onda |
| `EV_EXPORT_TRANSIENT` | falha transitória (rede, serviço ocupado) | retry com backoff |
| `EV_EXPORT_PERMANENT` | falha permanente (item corrompido além da política) | quarentena conforme §22 |
| `EV_REPORT_UNAVAILABLE` | relatório ausente/ilegível | reprovar a exportação (evidência incompleta) |
| `EV_OUTPUT_INCONSISTENT` | inventário ≠ relatório | bloquear; investigação obrigatória |
| `EV_CANCELLED` | cancelado por operador | estado terminal registrado |

Nenhum adapter lança exceção fora da taxonomia; erros nativos são
encapsulados com o texto original **sanitizado** (sem assunto, corpo,
credencial ou caminho sensível) como detalhe de diagnóstico.

## Compatibilidade e evolução

- O contrato é versionado (`IEvExportAdapter` v1); mudanças quebram-tudo
  exigem nova versão e migração explícita dos adapters.
- Adapter desconhecido, não assinado ou não certificado **não carrega**:
  a fábrica de adapters resolve exclusivamente a partir do relatório de
  capability discovery + matriz de certificação.
