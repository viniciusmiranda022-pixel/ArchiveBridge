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
/// Regra obrigatória de capacidade: volume planejado para o MESMO archive superior a 100 GB exige
/// <c>MICROSOFT_ASSESSMENT_REQUIRED</c>. Estritamente maior que 100 GiB — exatamente 100 GiB está no
/// limite (permitido). Não há override por configuração comum.
/// </summary>
public static class CapacityRule
{
    /// <summary>Limite de 100 GiB em bytes (100 × 1024³).</summary>
    public const long HundredGigabytesInBytes = 100L * 1024 * 1024 * 1024;

    /// <summary>Código de bloqueio registrado quando a avaliação é exigida.</summary>
    public const string AssessmentRequiredCode = "MICROSOFT_ASSESSMENT_REQUIRED";

    /// <summary>Código quando o volume está dentro do limite.</summary>
    public const string WithinLimitCode = "WITHIN_LIMIT";

    /// <summary>Avalia um volume total (bytes) contra o limite (estritamente maior ⇒ avaliação exigida).</summary>
    public static CapacityAssessmentResult Evaluate(long totalBytes) =>
        totalBytes > HundredGigabytesInBytes
            ? CapacityAssessmentResult.AssessmentRequired
            : CapacityAssessmentResult.WithinLimit;

    /// <summary>Código correspondente ao resultado.</summary>
    public static string CodeFor(CapacityAssessmentResult result) =>
        result == CapacityAssessmentResult.AssessmentRequired ? AssessmentRequiredCode : WithinLimitCode;
}

/// <summary>Avaliação de capacidade de um archive específico.</summary>
public sealed record ArchiveCapacityAssessment(
    string Mailbox,
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
/// entradas da mesma onda não contorna a regra: a soma é por archive (mailbox).
/// </summary>
public static class CapacityPlanner
{
    /// <summary>Avalia a seleção da onda, produzindo o relatório por archive.</summary>
    public static CapacityAssessmentReport Assess(WaveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var perArchive = selection.BytesByArchive()
            .Select(pair =>
            {
                var result = CapacityRule.Evaluate(pair.Value);
                return new ArchiveCapacityAssessment(pair.Key, pair.Value, result, CapacityRule.CodeFor(result));
            })
            .OrderBy(assessment => assessment.Mailbox, StringComparer.Ordinal)
            .ToList();

        return new CapacityAssessmentReport(perArchive);
    }
}
