namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Completude de evidência de UM certificate (AB-I6-013 item 4/10) — SEMPRE derivada das contagens
/// EXPLÍCITAS de itens <see cref="ReconciliationDisposition.IncompleteEvidence"/> da avaliação canônica
/// (Passo 3, mesmo read model de <see cref="ReconciliationWaveSummary"/>): um item
/// <c>IncompleteEvidence</c> É, por definição de domínio (ver <see cref="ReconciliationDisposition"/>),
/// exatamente a marca de evidência insuficiente/inconclusiva já materializada pelo Passo 3 — o certificate
/// nunca recalcula uma noção paralela de completude a partir de contadores brutos.
/// <para>
/// <see cref="IsComplete"/> é <see langword="true"/> SOMENTE quando 100% dos itens (PST + archive) da
/// avaliação estão resolvidos (nenhum <c>IncompleteEvidence</c>) — item 4: "Unknown/NotReported, evidência
/// ausente/stale ou cadeia não verificável nunca podem ser convertidos em sucesso terminal por default".
/// Uma onda SEM NENHUM item canônico (<see cref="TotalItemCount"/> igual a zero) nunca é tratada como
/// completa por vacuidade — não há evidência alguma a certificar (fail-closed).
/// </para>
/// </summary>
public sealed record ReconciliationCertificateEvidenceCompleteness(int TotalItemCount, int IncompleteItemCount)
{
    /// <summary>Deriva a completude a partir do resumo já computado pelo Passo 3.</summary>
    public static ReconciliationCertificateEvidenceCompleteness From(ReconciliationWaveSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var total = summary.PstMatched + summary.PstMismatch + summary.PstIncomplete + summary.PstBlockedIntegrity
            + summary.PstExtraInProvider + summary.ArchiveMatched + summary.ArchiveMismatch + summary.ArchiveIncomplete
            + summary.ArchiveBlockedIntegrity;
        var incomplete = summary.PstIncomplete + summary.ArchiveIncomplete;

        return new ReconciliationCertificateEvidenceCompleteness(total, incomplete);
    }

    /// <summary>
    /// <see langword="true"/> quando há ao menos um item canônico e nenhum deles é
    /// <see cref="ReconciliationDisposition.IncompleteEvidence"/>.
    /// </summary>
    public bool IsComplete => TotalItemCount > 0 && IncompleteItemCount == 0;

    /// <summary>Percentual de completude (0-100) — puramente informativo/auditável; nunca a única autoridade do gate (<see cref="IsComplete"/> é booleano e exato).</summary>
    public decimal CompletenessPercent => TotalItemCount == 0 ? 0m : 100m * (TotalItemCount - IncompleteItemCount) / TotalItemCount;
}
