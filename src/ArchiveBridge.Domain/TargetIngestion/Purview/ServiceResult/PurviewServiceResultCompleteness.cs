namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Avaliação de COMPLETUDE DA EVIDÊNCIA DO PROVIDER — estritamente limitada ao material deste Passo
/// (AB-I6-001 item 12). NUNCA um resultado de reconciliação final: <see cref="Domain.Reconciliation.ReconciliationOutcome"/>
/// (PASS/PASS_WITH_EXPLAINED_EXCEPTIONS/FAIL/DUPLICATE_RISK, runbook §26.3) permanece exclusivo dos
/// Passos futuros do EPIC-07, que ainda dependem de estatísticas EXO before/after, cálculo
/// expected-vs-observed, disposition de exceções e certificate. Este tipo responde apenas: "o provider já
/// forneceu evidência suficiente, por PST, para que a reconciliação real possa começar?".
/// </summary>
public enum PurviewServiceResultCompletenessOutcome
{
    /// <summary>Todo PST canônico da onda tem uma linha correlacionada com status/contadores conclusivos.</summary>
    CompleteForProviderEvidence,

    /// <summary>Um ou mais PSTs canônicos da onda ainda não têm nenhuma linha correlacionada no relatório.</summary>
    Incomplete,

    /// <summary>
    /// Todo PST canônico tem uma linha correlacionada, mas o serviço não expõe granularidade suficiente
    /// (status/contadores <c>Unknown</c>) para concluir sucesso/falha por PST (runbook §26.3 INCONCLUSIVE:
    /// "serviço não expõe granularidade suficiente").
    /// </summary>
    Inconclusive,
}

/// <summary>
/// Avalia a completude da evidência do provider a partir do resultado de correlação (AB-I6-001 item 12).
/// Função pura e determinística — nunca consulta stores, nunca produz <c>PASS</c>/certificate/conclusão de
/// onda.
/// </summary>
public static class PurviewServiceResultCompleteness
{
    /// <summary>Avalia a completude a partir da contagem canônica/correlacionada.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="result"/> não tem nenhum PST canônico (onda sem escopo a avaliar).</exception>
    public static PurviewServiceResultCompletenessOutcome Evaluate(PurviewServiceResultCorrelationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CanonicalCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(result), result.CanonicalCount, "A onda não tem nenhum PST canônico a avaliar.");
        }

        if (result.MatchedCount < result.CanonicalCount)
        {
            return PurviewServiceResultCompletenessOutcome.Incomplete;
        }

        return result.AnyMatchedRowIsInconclusive
            ? PurviewServiceResultCompletenessOutcome.Inconclusive
            : PurviewServiceResultCompletenessOutcome.CompleteForProviderEvidence;
    }
}
