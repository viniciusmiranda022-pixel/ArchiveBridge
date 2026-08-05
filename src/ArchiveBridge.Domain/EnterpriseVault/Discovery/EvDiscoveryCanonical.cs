using System.Globalization;

namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Canonicalização ÚNICA e determinística das coleções semânticas da descoberta, COMPARTILHADA pelo
/// <see cref="EvDiscoverySemanticFingerprint"/> e pelo serializer de evidência. Estratégia escolhida e
/// EXPLÍCITA: <b>ordenação TOTAL por TODOS os campos semânticos</b> (nunca por chave parcial) — de modo que
/// duas entradas com a mesma chave parcial e conteúdo diferente fiquem em ordem determinística, e inverter
/// qualquer coleção NUNCA altere o hash ou os bytes canônicos. Não se depende da ordem de entrada. A
/// maturidade distingue AUSENTE (nula) de todas-as-flags-falsas por um marcador de PRESENÇA — nunca se
/// converte <see langword="null"/> em <c>false</c> silenciosamente.
/// </summary>
public static class EvDiscoveryCanonical
{
    /// <summary>Separador de unidade (U+001F): não ocorre em códigos/razões/descrições — evita ambiguidade na chave.</summary>
    private const char Unit = '\u001f';

    /// <summary>Capacidades em ordem TOTAL: código, versão, disponibilidade, referência de evidência, razão de bloqueio.</summary>
    public static IReadOnlyList<EvCapability> OrderCapabilities(IEnumerable<EvCapability> capabilities) =>
        [.. capabilities
            .OrderBy(static c => c.CapabilityCode.Value, StringComparer.Ordinal)
            .ThenBy(static c => c.CapabilityVersion)
            .ThenBy(static c => (int)c.Availability)
            .ThenBy(static c => c.EvidenceReference, StringComparer.Ordinal)
            .ThenBy(static c => c.BlockingReason ?? string.Empty, StringComparer.Ordinal)];

    /// <summary>Achados em ordem TOTAL: código de resultado, capacidade, razão, categoria de erro.</summary>
    public static IReadOnlyList<EvDiscoveryFinding> OrderFindings(IEnumerable<EvDiscoveryFinding> findings) =>
        [.. findings
            .OrderBy(static f => f.ResultCode.Value, StringComparer.Ordinal)
            .ThenBy(static f => f.CapabilityCode?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static f => f.Reason, StringComparer.Ordinal)
            .ThenBy(static f => (int)f.ErrorCategory)];

    /// <summary>Requisitos em ordem TOTAL: código de capacidade, descrição.</summary>
    public static IReadOnlyList<EvAdapterRequirement> OrderRequirements(IEnumerable<EvAdapterRequirement> requirements) =>
        [.. requirements
            .OrderBy(static r => r.CapabilityCode.Value, StringComparer.Ordinal)
            .ThenBy(static r => r.Description, StringComparer.Ordinal)];

    /// <summary>Candidatos (AdapterIds) em ordem TOTAL ordinal.</summary>
    public static IReadOnlyList<string> OrderCandidates(IEnumerable<EvAdapterId> candidates) =>
        [.. candidates.Select(static c => c.Value).OrderBy(static v => v, StringComparer.Ordinal)];

    /// <summary>
    /// Avaliações em ordem TOTAL pela CHAVE CANÔNICA COMPLETA (todos os campos, incluindo sub-coleções já
    /// canonicalizadas) — não só por AdapterId. Duas avaliações de mesmo AdapterId com conteúdo diferente
    /// ficam em ordem determinística; inverter a lista não muda nada.
    /// </summary>
    public static IReadOnlyList<EvAdapterEvaluation> OrderEvaluations(IEnumerable<EvAdapterEvaluation> evaluations) =>
        [.. evaluations.OrderBy(EvaluationSortKey, StringComparer.Ordinal)];

    /// <summary>Tokens de maturidade com marcador de PRESENÇA (distingue ausente de todas-as-flags-falsas).</summary>
    public static IEnumerable<string> MaturityTokens(EvExportMaturity? maturity)
    {
        if (maturity is null)
        {
            yield return "matPresent";
            yield return "0";
            yield break;
        }

        yield return "matPresent";
        yield return "1";
        yield return Bit(maturity.RuntimeObserved);
        yield return Bit(maturity.OfficialDocumentation);
        yield return Bit(maturity.AutomatedFixtureValidated);
        yield return Bit(maturity.LaboratoryValidated);
    }

    private static string Bit(bool value) => value ? "1" : "0";

    /// <summary>Chave de ordenação TOTAL de uma avaliação: todos os campos semânticos canonicalizados.</summary>
    private static string EvaluationSortKey(EvAdapterEvaluation evaluation)
    {
        var parts = new List<string>
        {
            evaluation.AdapterId.Value,
            evaluation.AdapterVersion.ToString(CultureInfo.InvariantCulture),
            ((int)evaluation.Compatibility).ToString(CultureInfo.InvariantCulture),
            evaluation.Precedence.ToString(CultureInfo.InvariantCulture),
            evaluation.ProfileId ?? string.Empty,
        };
        parts.AddRange(MaturityTokens(evaluation.Maturity));
        foreach (var requirement in OrderRequirements(evaluation.Requirements))
        {
            parts.Add(requirement.CapabilityCode.Value);
            parts.Add(requirement.Description);
        }

        foreach (var capability in OrderCapabilities(evaluation.Capabilities))
        {
            parts.Add(capability.CapabilityCode.Value);
            parts.Add(capability.CapabilityVersion.ToString(CultureInfo.InvariantCulture));
            parts.Add(((int)capability.Availability).ToString(CultureInfo.InvariantCulture));
            parts.Add(capability.EvidenceReference);
            parts.Add(capability.BlockingReason ?? string.Empty);
        }

        foreach (var finding in OrderFindings(evaluation.Findings))
        {
            parts.Add(finding.ResultCode.Value);
            parts.Add(finding.CapabilityCode?.Value ?? string.Empty);
            parts.Add(finding.Reason);
            parts.Add(((int)finding.ErrorCategory).ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(Unit, parts);
    }
}
