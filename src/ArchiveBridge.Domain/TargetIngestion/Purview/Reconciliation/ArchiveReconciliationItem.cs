using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Disposition técnica explícita de UM archive/mailbox de destino, correlacionando os snapshots
/// <c>BeforeImport</c>/<c>AfterImport</c> canônicos (Passo 2) pela identidade server-side do archive
/// (AB-I6-007 item 8). Deltas são calculados SOMENTE quando ambos os lados da métrica são conhecidos
/// (item 9: "Se Before ou After for null/Unknown, o delta também é Unknown") — nunca por métrica isolada
/// fabricada.
/// </summary>
public sealed record ArchiveReconciliationItem(
    TargetArchiveId Archive,
    ReconciliationDisposition Disposition,
    bool BeforeCaptured,
    bool AfterCaptured,
    long? ItemCountDelta,
    long? TotalItemSizeBytesDelta);
