using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Correlaciona, de forma PURA e determinística, o conjunto ESPERADO de nomes remotos de PST (resolvido
/// server-side pela cadeia canônica <c>WaveEntry ↔ Binding ↔ PartitionExecution ↔ Upload manifest ↔
/// Mapping</c>, nunca fornecido pelo caller) contra as linhas OBSERVADAS já normalizadas/revalidadas do
/// service result do Purview (AB-I6-007 itens 2-3/5-7). Diferente de
/// <see cref="ServiceResult.PurviewServiceResultCorrelation"/> (que recusa o relatório INTEIRO fail-closed
/// quando encontra um item fora do conjunto canônico — gate de IMPORTAÇÃO), esta correlação nunca lança
/// para um item extra/ausente/inconclusivo: cada um vira uma disposition técnica EXPLÍCITA no resultado
/// (item 7: "não pode ser ignorado silenciosamente") — apenas uma entrada estruturalmente inválida (duas
/// linhas observadas com o MESMO nome remoto) falha fechado, pois não há como escolher qual delas é a
/// evidência sem descartar a outra silenciosamente.
/// </summary>
public static class ReconciliationPstCorrelation
{
    /// <summary>
    /// Correlaciona o conjunto esperado com as linhas observadas, devolvendo UM item por PST esperado
    /// (<see cref="ReconciliationDisposition.MatchedWithinEvidence"/>/<see cref="ReconciliationDisposition.Mismatch"/>/
    /// <see cref="ReconciliationDisposition.IncompleteEvidence"/> quando ausente do provider) seguido de UM
    /// item por observação extra (<see cref="ReconciliationDisposition.ExtraInProvider"/>) — nunca perde
    /// silenciosamente um lado.
    /// </summary>
    /// <exception cref="ReconciliationValidationException"><paramref name="observedRows"/> contém duas linhas com o mesmo nome remoto (fail-closed).</exception>
    public static IReadOnlyList<PstReconciliationItem> Correlate(
        IReadOnlyList<PurviewRemotePstName> expectedRemoteNames, IReadOnlyList<PurviewServiceResultRow> observedRows)
    {
        ArgumentNullException.ThrowIfNull(expectedRemoteNames);
        ArgumentNullException.ThrowIfNull(observedRows);

        var byRemoteName = new Dictionary<string, PurviewServiceResultRow>(StringComparer.Ordinal);
        foreach (var row in observedRows)
        {
            if (!byRemoteName.TryAdd(row.RemoteName.Value, row))
            {
                throw new ReconciliationValidationException(
                    $"Linha de service result observada duplicada para o mesmo nome remoto '{row.RemoteName.Value}' " +
                    "— correlação de reconciliação recusada (fail-closed).");
            }
        }

        var expectedSet = new HashSet<string>(expectedRemoteNames.Select(name => name.Value), StringComparer.Ordinal);
        var items = new List<PstReconciliationItem>(expectedRemoteNames.Count + observedRows.Count);

        foreach (var expected in expectedRemoteNames)
        {
            if (!byRemoteName.TryGetValue(expected.Value, out var row))
            {
                // PST esperado ausente do provider result: ausência de dado é Unknown/Incomplete, nunca
                // Mismatch inventado (item 5) e nunca descartado silenciosamente (item 7).
                items.Add(new PstReconciliationItem(
                    expected, ReconciliationDisposition.IncompleteEvidence, observedStatus: null,
                    importedItemCount: null, importedSizeBytes: null, skippedItemCount: null, corruptedItemCount: null));
                continue;
            }

            items.Add(new PstReconciliationItem(
                expected, Classify(row), row.Status, row.ImportedItemCount, row.ImportedSizeBytes,
                row.SkippedItemCount, row.CorruptedItemCount));
        }

        foreach (var row in observedRows)
        {
            if (!expectedSet.Contains(row.RemoteName.Value))
            {
                // Item extra no provider result: nunca ignorado silenciosamente (item 7) — aparece como
                // exceção de reconciliação explícita.
                items.Add(new PstReconciliationItem(
                    row.RemoteName, ReconciliationDisposition.ExtraInProvider, row.Status, row.ImportedItemCount,
                    row.ImportedSizeBytes, row.SkippedItemCount, row.CorruptedItemCount));
            }
        }

        return items;
    }

    // Um `Purview ImportCompleted/Complete` isolado não equivale a sucesso da reconciliação ("Regras
    // mínimas de avaliação" do work order): Matched exige status conclusivo E contadores obrigatórios
    // presentes; Mismatch exige divergência CONCRETA (Failed/SkippedOrCorrupted), nunca inferida de
    // ausência.
    private static ReconciliationDisposition Classify(PurviewServiceResultRow row)
    {
        if (row.Status == PurviewServiceResultRowStatus.Unknown || row.ImportedItemCount is null || row.ImportedSizeBytes is null)
        {
            return ReconciliationDisposition.IncompleteEvidence;
        }

        return row.Status switch
        {
            PurviewServiceResultRowStatus.Succeeded => ReconciliationDisposition.MatchedWithinEvidence,
            PurviewServiceResultRowStatus.Failed or PurviewServiceResultRowStatus.SkippedOrCorrupted => ReconciliationDisposition.Mismatch,
            _ => ReconciliationDisposition.IncompleteEvidence,
        };
    }
}
