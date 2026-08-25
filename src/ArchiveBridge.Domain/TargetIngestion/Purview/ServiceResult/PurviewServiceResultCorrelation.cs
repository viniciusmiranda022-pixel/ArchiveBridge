using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Resultado da correlação 1:1 entre as linhas do relatório e o conjunto canônico de PSTs da onda
/// (AB-I6-001 item 8): quantos PSTs canônicos existem, quantos foram efetivamente cobertos por uma linha
/// do relatório, e se alguma linha coberta ficou com status/contadores <c>Unknown</c> (insuficiente para
/// concluir sucesso/falha daquele PST).
/// </summary>
public sealed record PurviewServiceResultCorrelationResult(int CanonicalCount, int MatchedCount, bool AnyMatchedRowIsInconclusive);

/// <summary>
/// Correlaciona, de forma PURA e determinística, as linhas JÁ PARSEADAS de um service result report com o
/// conjunto canônico de nomes remotos de PST da onda — a MESMA cadeia
/// <c>WaveEntry ↔ Binding ↔ PartitionExecution ↔ Upload manifest ↔ Mapping</c> já resolvida server-side
/// pela Application (AB-I6-001 item 8). Nunca aceita correlação por ordem/posição: a chave é sempre o nome
/// remoto exato. Um item cujo nome remoto não pertence ao conjunto canônico da onda (desconhecido, de
/// outra onda, ou de outro tenant/projeto) falha fechado o relatório inteiro — nunca é descartado
/// silenciosamente.
/// </summary>
public static class PurviewServiceResultCorrelation
{
    /// <summary>
    /// Correlaciona as linhas com o conjunto canônico. Quando <paramref name="reportDeclaresCompleteness"/>
    /// é verdadeiro (o próprio relatório afirmou, via diretiva, cobrir todos os PSTs) e a cobertura não é
    /// EXATA (conjunto extra ou ausente), falha fechado — o relatório afirmou uma completude que não
    /// entrega. Quando o relatório nunca afirmou completude, um subconjunto ausente é aceitável aqui (a
    /// avaliação de completude, separada, marcará <c>Incomplete</c>).
    /// </summary>
    /// <exception cref="PurviewServiceResultCorrelationException">
    /// Item com nome remoto desconhecido do escopo canônico da onda, ou completude declarada não cumprida.
    /// </exception>
    public static PurviewServiceResultCorrelationResult Correlate(
        IReadOnlyCollection<PurviewRemotePstName> canonicalRemoteNames,
        IReadOnlyList<PurviewServiceResultRow> rows,
        bool reportDeclaresCompleteness)
    {
        ArgumentNullException.ThrowIfNull(canonicalRemoteNames);
        ArgumentNullException.ThrowIfNull(rows);

        var canonicalSet = new HashSet<string>(canonicalRemoteNames.Select(name => name.Value), StringComparer.Ordinal);
        var matched = 0;
        var anyInconclusive = false;

        foreach (var row in rows)
        {
            if (!canonicalSet.Contains(row.RemoteName.Value))
            {
                throw new PurviewServiceResultCorrelationException(
                    "Uma linha do relatório referencia um PST fora do conjunto canônico ATUAL da onda " +
                    "(desconhecido, de outra onda ou de outro escopo) — correlação recusada (fail-closed).");
            }

            matched++;
            if (row.Status == PurviewServiceResultRowStatus.Unknown
                || row.ImportedItemCount is null
                || row.ImportedSizeBytes is null)
            {
                anyInconclusive = true;
            }
        }

        if (reportDeclaresCompleteness && matched != canonicalSet.Count)
        {
            throw new PurviewServiceResultCorrelationException(
                $"O relatório declara cobrir todos os PSTs da onda, mas correlaciona {matched} de {canonicalSet.Count} " +
                "PSTs canônicos — completude declarada não cumprida (fail-closed).");
        }

        return new PurviewServiceResultCorrelationResult(canonicalSet.Count, matched, anyInconclusive);
    }
}
