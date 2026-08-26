using ArchiveBridge.Domain.Performance.SloEvidence;

namespace ArchiveBridge.Domain.Performance;

/// <summary>
/// A taxa de crescimento de ~24 GB/dia por mailbox citada no runbook §46 (fonte: Microsoft) — registrada
/// EXCLUSIVAMENTE como referência/típica, nunca como SLA (AB-I7-003 §5, acceptance criterion 5). Este tipo
/// só expõe o valor através de <see cref="AsReferenceEstimate"/>, que produz um
/// <see cref="ReferenceEstimate"/> — nunca um <see cref="ObservedMetric"/> nem um <see cref="ContractualSla"/>
/// configurado.
/// </summary>
public static class MailboxGrowthReference
{
    /// <summary>~24 GB/dia por mailbox (GB decimal/SI, 10⁹ bytes) — taxa TÍPICA, não SLA.</summary>
    public const long TypicalBytesPerMailboxPerDay = 24_000_000_000L;

    /// <summary>Fonte versionada citada pelo runbook para esta taxa.</summary>
    public const string SourceCitation =
        "docs/runbook/06-parte-vi-plano-desenvolvimento.md §46 — Microsoft, taxa típica (~24 GB/dia/mailbox), não SLA.";

    /// <summary>Nome estável da métrica quando referenciada em evidência SLO.</summary>
    public const string MetricName = "MailboxGrowthBytesPerDay";

    /// <summary>Produz a referência/estimativa citável — nunca uma medição, nunca um SLA.</summary>
    public static ReferenceEstimate AsReferenceEstimate() =>
        new(MetricName, TypicalBytesPerMailboxPerDay, "bytes/day", SourceCitation);
}
