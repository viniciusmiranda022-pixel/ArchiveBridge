using ArchiveBridge.Domain.Reconciliation;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Regras semânticas PURAS e determinísticas do reconciliation certificate (AB-I6-013, "Regras semânticas
/// mínimas" do work order) — nunca depende de infraestrutura, RBAC concreto ou I/O. Usa exclusivamente a
/// taxonomia já documentada no runbook (<see cref="ReconciliationOutcome"/>, runbook §26.3), sem inventar
/// estados alternativos equivalentes.
/// <para>
/// Precedência do resultado (da mais para a menos bloqueadora — cada gate é verificado NA ORDEM abaixo e o
/// primeiro que se aplica decide o resultado; nenhum resultado "melhor" jamais mascara um gate anterior):
/// </para>
/// <list type="number">
/// <item><description><see cref="ReconciliationOutcome.DuplicateRisk"/> — precedência bloqueadora sobre
/// qualquer outro resultado (item 63 do work order): um risco de duplicidade comprovado é sempre o desfecho
/// reportado, independentemente do estado de completude/exceções.</description></item>
/// <item><description>Nenhum item canônico na avaliação (onda sem evidência a certificar) —
/// <see cref="ReconciliationOutcome.Inconclusive"/> fail-closed (nunca completo por vacuidade).</description></item>
/// <item><description>Qualquer item <see cref="ReconciliationDisposition.BlockedIntegrity"/> —
/// <see cref="ReconciliationOutcome.Fail"/>: indeclinável, nunca dispositionable (item 5), prevalece sobre
/// qualquer disposition eventualmente registrada sobre OUTROS itens da mesma avaliação.</description></item>
/// <item><description>Evidence completeness abaixo de 100% (qualquer item
/// <see cref="ReconciliationDisposition.IncompleteEvidence"/>, MESMO que uma disposition humana o tenha
/// marcado <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> no workflow do Passo 4) —
/// <see cref="ReconciliationOutcome.Inconclusive"/> (item 4/36: aceitar o RISCO OPERACIONAL de uma exceção
/// IncompleteEvidence nunca torna a EVIDÊNCIA completa; os dois conceitos são deliberadamente
/// independentes).</description></item>
/// <item><description>Qualquer exceção técnica <see cref="ReconciliationDisposition.Mismatch"/>/
/// <see cref="ReconciliationDisposition.ExtraInProvider"/> SEM decisão vigente
/// <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> (Pending, RemediationRequired,
/// Rejected ou qualquer outro estado que não seja a aceitação explícita) —
/// <see cref="ReconciliationOutcome.Fail"/> (item 6/40: nunca mascarada por comentário/disposition
/// parcial).</description></item>
/// <item><description>Ao menos uma exceção <see cref="ReconciliationDisposition.Mismatch"/>/
/// <see cref="ReconciliationDisposition.ExtraInProvider"/> com <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/>
/// vigente, evidência 100% completa e nenhum BlockedIntegrity/DuplicateRisk —
/// <see cref="ReconciliationOutcome.PassWithExplainedExceptions"/>.</description></item>
/// <item><description>Nenhuma exceção material, evidência 100% completa —
/// <see cref="ReconciliationOutcome.Pass"/> (item 7).</description></item>
/// </list>
/// </summary>
public static class ReconciliationCertificateRules
{
    /// <summary>Determina o resultado canônico do certificate a partir do estado técnico/decisório já resolvido server-side.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="completeness"/> ou <paramref name="backlog"/> são nulos.</exception>
    public static ReconciliationOutcome DetermineResult(
        ReconciliationCertificateEvidenceCompleteness completeness,
        ReconciliationExceptionWaveBacklog backlog,
        bool duplicateRiskDetected)
    {
        ArgumentNullException.ThrowIfNull(completeness);
        ArgumentNullException.ThrowIfNull(backlog);

        if (duplicateRiskDetected)
        {
            return ReconciliationOutcome.DuplicateRisk;
        }

        if (completeness.TotalItemCount == 0)
        {
            return ReconciliationOutcome.Inconclusive;
        }

        if (HasBlockedIntegrity(backlog))
        {
            return ReconciliationOutcome.Fail;
        }

        if (!completeness.IsComplete)
        {
            return ReconciliationOutcome.Inconclusive;
        }

        var technicalExceptions = CountTechnicalExceptions(backlog);
        var unexplained = CountUnexplainedTechnicalExceptions(backlog);
        if (unexplained > 0)
        {
            return ReconciliationOutcome.Fail;
        }

        return technicalExceptions > 0 ? ReconciliationOutcome.PassWithExplainedExceptions : ReconciliationOutcome.Pass;
    }

    /// <summary>
    /// Constrói o resumo estruturado de desvios (item 10) a partir do backlog de exceções vigente — cada
    /// entrada não-Matched da avaliação recebe exatamente um <see cref="ReconciliationCertificateDeviationCode"/>
    /// determinístico. Nunca inclui itens <see cref="ReconciliationDisposition.MatchedWithinEvidence"/> (o
    /// backlog já os exclui — item 11 do Passo 4).
    /// </summary>
    public static IReadOnlyList<ReconciliationCertificateDeviationEntry> BuildDeviationSummary(ReconciliationExceptionWaveBacklog backlog)
    {
        ArgumentNullException.ThrowIfNull(backlog);

        var entries = new List<ReconciliationCertificateDeviationEntry>(backlog.Entries.Count);
        foreach (var entry in backlog.Entries)
        {
            var code = ClassifyDeviation(entry);
            entries.Add(new ReconciliationCertificateDeviationEntry(entry.ItemKind, entry.ItemKey, entry.TechnicalDisposition, code));
        }

        return entries;
    }

    private static bool HasBlockedIntegrity(ReconciliationExceptionWaveBacklog backlog) =>
        backlog.Entries.Any(entry => entry.TechnicalDisposition == ReconciliationDisposition.BlockedIntegrity);

    private static bool IsTechnicalException(ReconciliationExceptionBacklogEntry entry) =>
        entry.TechnicalDisposition is ReconciliationDisposition.Mismatch or ReconciliationDisposition.ExtraInProvider;

    private static int CountTechnicalExceptions(ReconciliationExceptionWaveBacklog backlog) =>
        backlog.Entries.Count(IsTechnicalException);

    private static int CountUnexplainedTechnicalExceptions(ReconciliationExceptionWaveBacklog backlog) =>
        backlog.Entries.Count(entry => IsTechnicalException(entry) && entry.CurrentStatus != ReconciliationExceptionDecisionStatus.AcceptedException);

    private static ReconciliationCertificateDeviationCode ClassifyDeviation(ReconciliationExceptionBacklogEntry entry) =>
        entry.TechnicalDisposition switch
        {
            ReconciliationDisposition.IncompleteEvidence => ReconciliationCertificateDeviationCode.IncompleteEvidence,
            ReconciliationDisposition.BlockedIntegrity => ReconciliationCertificateDeviationCode.BlockedIntegrity,
            _ => entry.CurrentStatus == ReconciliationExceptionDecisionStatus.AcceptedException
                ? ReconciliationCertificateDeviationCode.ExplainedException
                : ReconciliationCertificateDeviationCode.UnexplainedException,
        };
}
