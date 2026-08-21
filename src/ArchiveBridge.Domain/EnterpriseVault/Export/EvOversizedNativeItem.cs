using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>
/// Classificação de item nativo excedente segundo o cmdlet <c>Export-EVArchive</c> (runbook §16.4): um item
/// acima de 250 MB pode ficar fora do PST; acima de 2 GB pode ser exportado apenas em formato NATIVE. Nunca
/// silenciosamente omitido do certificado final — sempre materializado como estado lógico explícito.
/// </summary>
public enum EvOversizedNativeItemClassification
{
    /// <summary>Item entre o limiar PST (250 MB) e o limiar NATIVE (2 GB) — pode ficar fora do PST.</summary>
    ExceedsPstItemThreshold,

    /// <summary>Item acima de 2 GB — exportável apenas em formato nativo pelo exporter.</summary>
    ExceedsNativeExportThreshold,
}

/// <summary>
/// UM item nativo classificado como oversized durante a exportação (AB-4C-005 item 14). Registra apenas o
/// vínculo/diagnóstico estruturado — <see cref="ExternalItemReference"/> é uma referência OPACA emitida
/// pelo exporter (nunca subject/remetente/destinatário/corpo/anexo).
/// </summary>
public sealed record EvOversizedNativeItem
{
    private const int ExternalItemReferenceMaxLength = 300;
    private const int DiagnosticMaxLength = 300;

    /// <summary>Limiar documentado (§16.4) acima do qual um item pode ficar fora do PST.</summary>
    public const long PstItemThresholdBytes = 250L * 1024 * 1024;

    /// <summary>Limiar documentado (§16.4) acima do qual um item só é exportável em formato nativo.</summary>
    public const long NativeExportThresholdBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Cria um registro de item oversized validado (fail-closed sobre a forma dos campos).</summary>
    public EvOversizedNativeItem(
        string externalItemReference, long sizeBytes, EvOversizedNativeItemClassification classification,
        string? diagnostic, DateTimeOffset detectedAtUtc)
    {
        ExternalItemReference = TextValue.Require(externalItemReference, nameof(externalItemReference), ExternalItemReferenceMaxLength);
        if (sizeBytes <= PstItemThresholdBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes), sizeBytes, $"Item oversized deve exceder {PstItemThresholdBytes} bytes (limiar PST, §16.4).");
        }

        SizeBytes = sizeBytes;
        Classification = classification;
        Diagnostic = string.IsNullOrWhiteSpace(diagnostic)
            ? null
            : TextValue.Require(diagnostic, nameof(diagnostic), DiagnosticMaxLength);
        DetectedAtUtc = detectedAtUtc;
    }

    /// <summary>Referência opaca do item emitida pelo exporter (nunca conteúdo de mailbox).</summary>
    public string ExternalItemReference { get; }

    /// <summary>Tamanho observado do item, em bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>Classificação de excedência (§16.4).</summary>
    public EvOversizedNativeItemClassification Classification { get; }

    /// <summary>Diagnóstico curto e estruturado (nunca mensagem livre com PII).</summary>
    public string? Diagnostic { get; }

    /// <summary>Instante em que a exceção foi observada.</summary>
    public DateTimeOffset DetectedAtUtc { get; }

    /// <summary>Classifica um tamanho observado; <see langword="null"/> quando abaixo do limiar PST (não é oversized).</summary>
    public static EvOversizedNativeItemClassification? Classify(long sizeBytes) => sizeBytes switch
    {
        <= PstItemThresholdBytes => null,
        <= NativeExportThresholdBytes => EvOversizedNativeItemClassification.ExceedsPstItemThreshold,
        _ => EvOversizedNativeItemClassification.ExceedsNativeExportThreshold,
    };
}
