using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Hash agregado, determinístico e ORDEM-INDEPENDENTE do conjunto de decisões VIGENTES (uma por item) de uma
/// versão de avaliação — usado EXCLUSIVAMENTE pela persistência do reconciliation certificate (AB-I6-013
/// item 17/49) para detectar, sob lock, se qualquer disposition mudou entre o instante em que a Application
/// resolveu o candidato de certificate e o instante em que a store tenta persisti-lo: uma emissão nunca
/// produz um certificate baseado em snapshot misto de dispositions antigas e novas. Cobre exclusivamente a
/// identidade do item e a <see cref="ReconciliationExceptionDecision.DecisionFingerprint"/> vigente sobre
/// ele — nunca o conteúdo pleno da decisão (já coberto, quando relevante ao resultado, pelo resumo de
/// desvios do certificate).
/// </summary>
public static class ReconciliationExceptionDecisionsStateHash
{
    private const string HashPrefix = "archivebridge.purview.reconciliation-certificate-decisions-state.v1";

    /// <summary>Calcula o hash a partir das decisões vigentes, ordenadas deterministicamente por (ItemKind, ItemKey) — nunca pela ordem de leitura.</summary>
    public static Sha256Hash Compute(IReadOnlyList<ReconciliationExceptionDecision> currentDecisions)
    {
        ArgumentNullException.ThrowIfNull(currentDecisions);

        var parts = new List<string> { HashPrefix, currentDecisions.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var decision in currentDecisions
            .OrderBy(d => (int)d.ItemKind)
            .ThenBy(d => d.ItemKey, StringComparer.Ordinal))
        {
            parts.Add(((int)decision.ItemKind).ToString(CultureInfo.InvariantCulture));
            parts.Add(decision.ItemKey);
            parts.Add(decision.DecisionFingerprint.Value);
        }

        return DeterministicHash.Compute(parts);
    }
}
