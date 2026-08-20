using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Política de particionamento (Slice 4B, Passo 2). Os limites NÃO são inventados aqui: são exatamente os
/// documentados no runbook §20.1 ("Política default") — <c>TargetPartBytes = 18 GiB</c> e
/// <c>HardPartBytes = 20 GB</c>. As unidades seguem literalmente o texto do runbook: <b>GiB = 1024³</b>
/// (binário, como em §16.3, onde o default de <c>-MaxPSTSizeMB</c> é 18432 MB = 18 GiB) e <b>GB = 10⁹</b>
/// (decimal, a convenção da recomendação do destino citada no mesmo parágrafo). A leitura decimal do limite
/// duro é também a mais conservadora das duas (20·10⁹ &lt; 20·1024³), portanto fail-closed em caso de dúvida.
/// <para>
/// A política participa da IDENTIDADE do plano por meio de <see cref="Fingerprint"/> (runbook §20.2:
/// <c>canonicalJson(partitionPolicy)</c>): mudar qualquer limite muda o fingerprint, muda o
/// <c>planHash</c> e portanto NUNCA reaproveita um plano calculado sob outra política.
/// </para>
/// </summary>
public sealed record PartitionPolicy
{
    /// <summary>Tamanho-alvo por parte documentado no runbook §20.1: 18 GiB.</summary>
    public const long RunbookTargetPartBytes = 18L * 1024 * 1024 * 1024;

    /// <summary>Limite duro por parte documentado no runbook §20.1: 20 GB (decimal, 10⁹).</summary>
    public const long RunbookHardPartBytes = 20_000_000_000L;

    private PartitionPolicy(long targetPartBytes, long hardPartBytes)
    {
        TargetPartBytes = targetPartBytes;
        HardPartBytes = hardPartBytes;

        // JSON canônico: chaves em ordem alfabética, sem espaços, inteiros em cultura invariante. É a
        // entrada estável de canonicalJson(partitionPolicy) do runbook §20.2 — qualquer mudança de valor
        // (ou de ordem/formatação) mudaria o fingerprint, então a forma é fixada aqui e coberta por teste.
        CanonicalJson = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"hardPartBytes\":{hardPartBytes},\"targetPartBytes\":{targetPartBytes}}}");
    }

    /// <summary>
    /// Cria uma política explícita. Fail-closed: limites não positivos, ou alvo acima do limite duro,
    /// são recusados — nunca "corrigidos" silenciosamente para um valor arbitrário.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Limite não positivo.</exception>
    /// <exception cref="ArgumentException">Alvo maior que o limite duro.</exception>
    public static PartitionPolicy Create(long targetPartBytes, long hardPartBytes)
    {
        if (targetPartBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPartBytes), "TargetPartBytes deve ser > 0.");
        }

        if (hardPartBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardPartBytes), "HardPartBytes deve ser > 0.");
        }

        if (targetPartBytes > hardPartBytes)
        {
            throw new ArgumentException("TargetPartBytes não pode exceder HardPartBytes.", nameof(targetPartBytes));
        }

        return new PartitionPolicy(targetPartBytes, hardPartBytes);
    }

    /// <summary>Política default DOCUMENTADA no runbook §20.1 (18 GiB alvo / 20 GB duro).</summary>
    public static PartitionPolicy RunbookDefault { get; } = new(RunbookTargetPartBytes, RunbookHardPartBytes);

    /// <summary>Tamanho-alvo por parte, em bytes.</summary>
    public long TargetPartBytes { get; }

    /// <summary>Limite duro por parte, em bytes (nenhuma parte planejada pode excedê-lo).</summary>
    public long HardPartBytes { get; }

    /// <summary>Forma canônica e estável da política (entrada de <see cref="Fingerprint"/>).</summary>
    public string CanonicalJson { get; }

    /// <summary>Fingerprint determinístico da política — participa da identidade do plano (runbook §20.2).</summary>
    public Sha256Hash Fingerprint =>
        DeterministicHash.Compute(["archivebridge.pst.partition-policy.v1", CanonicalJson]);
}
