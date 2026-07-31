using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Planning;

/// <summary>Resultado da avaliação de capacidade de um archive.</summary>
public enum CapacityAssessmentResult
{
    /// <summary>Dentro do limite; a onda pode prosseguir.</summary>
    WithinLimit,

    /// <summary>Acima do limite; exige avaliação/decisão explícita (a onda fica Blocked).</summary>
    AssessmentRequired,
}

/// <summary>
/// Regra obrigatória de capacidade: volume planejado para o MESMO archive superior a 100 GB
/// (decimais, 100 × 10⁹ bytes) exige <c>MICROSOFT_ASSESSMENT_REQUIRED</c>. Estritamente maior que
/// 100 GB — exatamente 100.000.000.000 bytes está no limite (permitido). Não há override por
/// configuração comum. A unidade é GB decimal (SI), não GiB binário.
/// </summary>
public static class CapacityRule
{
    /// <summary>Limite de 100 GB decimais em bytes (100 × 1.000.000.000).</summary>
    public const long OneHundredGigabytesInBytes = 100_000_000_000L;

    /// <summary>Código de bloqueio registrado quando a avaliação é exigida.</summary>
    public const string AssessmentRequiredCode = "MICROSOFT_ASSESSMENT_REQUIRED";

    /// <summary>Código quando o volume está dentro do limite.</summary>
    public const string WithinLimitCode = "WITHIN_LIMIT";

    /// <summary>Avalia um volume total (bytes) contra o limite (estritamente maior ⇒ avaliação exigida).</summary>
    public static CapacityAssessmentResult Evaluate(long totalBytes) =>
        totalBytes > OneHundredGigabytesInBytes
            ? CapacityAssessmentResult.AssessmentRequired
            : CapacityAssessmentResult.WithinLimit;

    /// <summary>Código correspondente ao resultado.</summary>
    public static string CodeFor(CapacityAssessmentResult result) =>
        result == CapacityAssessmentResult.AssessmentRequired ? AssessmentRequiredCode : WithinLimitCode;
}

/// <summary>Avaliação de capacidade de um archive específico (por identidade canônica).</summary>
public sealed record ArchiveCapacityAssessment(
    TargetArchiveId Archive,
    long TotalBytes,
    CapacityAssessmentResult Result,
    string RuleCode);

/// <summary>Relatório de capacidade da onda: uma avaliação por archive e o veredito agregado.</summary>
public sealed record CapacityAssessmentReport(IReadOnlyList<ArchiveCapacityAssessment> PerArchive)
{
    /// <summary>Verdadeiro se QUALQUER archive exige avaliação (a onda deve ficar Blocked).</summary>
    public bool AssessmentRequired =>
        PerArchive.Any(assessment => assessment.Result == CapacityAssessmentResult.AssessmentRequired);
}

/// <summary>
/// Serviço de domínio que aplica a regra de 100 GB por archive. Agrupar/dividir artificialmente as
/// entradas da mesma onda não contorna a regra: a soma é por identidade canônica do archive
/// (<see cref="TargetArchiveId"/>), então nem a variação de caixa nem aliases resolvidos ao mesmo
/// destino escapam do limite.
/// </summary>
public static class CapacityPlanner
{
    /// <summary>Avalia a seleção da onda, produzindo o relatório por archive (falha em overflow de soma).</summary>
    public static CapacityAssessmentReport Assess(WaveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var perArchive = selection.BytesByArchive()
            .Select(pair =>
            {
                var result = CapacityRule.Evaluate(pair.Value);
                return new ArchiveCapacityAssessment(pair.Key, pair.Value, result, CapacityRule.CodeFor(result));
            })
            .OrderBy(assessment => assessment.Archive.Value, StringComparer.Ordinal)
            .ToList();

        return new CapacityAssessmentReport(perArchive);
    }
}
