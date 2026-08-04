using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Configuração COMPLETA que enquadra uma descoberta e cujo hash identifica a reserva. Inclui o ambiente,
/// a versão/hash de configuração do projeto esperados, a versão da política, a lista ORDENADA de
/// capacidades exigidas, os limites (bytes/timeout) e as versões de esquema, catálogo de sondas e catálogo
/// de adapters — de modo que uma mudança em qualquer um desses fatores produza um <see cref="ComputeHash"/>
/// diferente e NÃO reutilize uma reserva incompatível.
/// </summary>
public sealed record EvDiscoveryConfiguration(
    EvEnvironmentId EnvironmentId,
    int ExpectedProjectConfigurationVersion,
    Sha256Hash ExpectedConfigurationHash,
    int PolicyVersion,
    IReadOnlyList<EvCapabilityCode> RequiredForReady,
    long MaxOutputBytes,
    TimeSpan ProbeTimeout,
    int DiscoverySchemaVersion,
    int ProbeCatalogVersion,
    int AdapterCatalogVersion)
{
    /// <summary>Constrói a configuração a partir da política e do contexto de projeto esperado.</summary>
    public static EvDiscoveryConfiguration For(
        EvEnvironmentId environmentId, EvDiscoveryPolicy policy, int expectedProjectConfigurationVersion, Sha256Hash expectedConfigurationHash)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new EvDiscoveryConfiguration(
            environmentId,
            expectedProjectConfigurationVersion,
            expectedConfigurationHash,
            policy.PolicyVersion,
            [.. policy.RequiredForReady.OrderBy(static code => code.Value, StringComparer.Ordinal)],
            policy.MaxOutputBytes,
            policy.ProbeTimeout,
            EvDiscoverySchema.Version,
            EvDiscoverySchema.ProbeCatalogVersion,
            EvDiscoverySchema.AdapterCatalogVersion);
    }

    /// <summary>Hash determinístico da configuração completa.</summary>
    public Sha256Hash ComputeHash() => DeterministicHash.Compute(
    [
        "env", EnvironmentId.Value.ToString("N"),
        "projCfgVer", ExpectedProjectConfigurationVersion.ToString(CultureInfo.InvariantCulture),
        "projCfgHash", ExpectedConfigurationHash.Value,
        "policyVer", PolicyVersion.ToString(CultureInfo.InvariantCulture),
        "required", string.Join(",", RequiredForReady.Select(static code => code.Value)),
        "maxBytes", MaxOutputBytes.ToString(CultureInfo.InvariantCulture),
        "timeoutTicks", ProbeTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
        "schema", DiscoverySchemaVersion.ToString(CultureInfo.InvariantCulture),
        "probeCatalog", ProbeCatalogVersion.ToString(CultureInfo.InvariantCulture),
        "adapterCatalog", AdapterCatalogVersion.ToString(CultureInfo.InvariantCulture),
    ]);
}

/// <summary>
/// Impressão digital SEMÂNTICA COMPLETA de uma execução de descoberta. Inclui, de forma canônica e com
/// ordenação ORDINAL EXPLÍCITA (o mesmo conteúdo semântico produz o mesmo hash independentemente da ordem
/// das coleções): identidade integral do ambiente; todas as capacidades; a assinatura normalizada e seu
/// hash; a SELEÇÃO completa (desfecho, candidatos ordenados, adapter selecionado, achados da seleção); e
/// CADA avaliação de adapter por inteiro — compatibilidade, precedência, perfil, MATURIDADE
/// (runtime/documentação/fixtures/laboratório), requisitos ordenados, capacidades declaradas ordenadas e
/// achados ordenados; além do status, do código de resultado, da versão de esquema e de cada achado com
/// sua <see cref="EvErrorCategory"/>. Qualquer diferença semântica muda o hash.
/// </summary>
public static class EvDiscoverySemanticFingerprint
{
    /// <summary>Computa o hash semântico completo da execução (canônico, ordenação ordinal explícita).</summary>
    public static Sha256Hash Compute(EvDiscoveryRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parts = new List<string>
        {
            "schema", result.CapabilitySet.DiscoverySchemaVersion.ToString(CultureInfo.InvariantCulture),
            "status", result.Status.ToString(),
            "resultCode", result.ResultCode.Value,
            "env", result.Environment.EnvironmentId.Value.ToString("N"),
            "site", result.Environment.SiteName,
            "server", result.Environment.DirectoryServer,
            "observed", result.Environment.ObservedVersion,
            "product", result.Environment.ProductVersion,
            "source", result.Environment.DiscoverySource,
            "adapter", result.CapabilitySet.AdapterId?.Value ?? "<none>",
            "adapterVer", (result.CapabilitySet.AdapterVersion ?? 0).ToString(CultureInfo.InvariantCulture),
        };

        foreach (var capability in OrderCapabilities(result.CapabilitySet.Capabilities))
        {
            AppendCapability(parts, "cap", capability);
        }

        parts.Add("sig");
        parts.Add(result.Signature?.SignatureHash.Value ?? "<none>");
        parts.Add(result.Signature is { } signature ? string.Join(",", signature.Parameters) : string.Empty);

        // Seleção: desfecho + adapter selecionado + candidatos ORDENADOS + achados da seleção ORDENADOS.
        parts.Add("selOutcome");
        parts.Add(result.Selection.Outcome.ToString());
        parts.Add("selected");
        parts.Add(result.Selection.Selected?.AdapterId.Value ?? "<none>");
        foreach (var candidate in result.Selection.Candidates.Select(static c => c.Value).OrderBy(static v => v, StringComparer.Ordinal))
        {
            parts.Add("cand");
            parts.Add(candidate);
        }

        foreach (var finding in OrderFindings(result.Selection.Findings))
        {
            AppendFinding(parts, "selFind", finding);
        }

        // Avaliações de adapter COMPLETAS (ordenadas por AdapterId).
        foreach (var evaluation in result.Selection.Evaluations.OrderBy(static e => e.AdapterId.Value, StringComparer.Ordinal))
        {
            parts.Add("adp");
            parts.Add(evaluation.AdapterId.Value);
            parts.Add(evaluation.AdapterVersion.ToString(CultureInfo.InvariantCulture));
            parts.Add(evaluation.Compatibility.ToString());
            parts.Add(evaluation.Precedence.ToString(CultureInfo.InvariantCulture));
            parts.Add(evaluation.ProfileId ?? string.Empty);

            parts.Add("mat");
            parts.Add(Bit(evaluation.Maturity?.RuntimeObserved));
            parts.Add(Bit(evaluation.Maturity?.OfficialDocumentation));
            parts.Add(Bit(evaluation.Maturity?.AutomatedFixtureValidated));
            parts.Add(Bit(evaluation.Maturity?.LaboratoryValidated));

            foreach (var requirement in evaluation.Requirements
                .OrderBy(static r => r.CapabilityCode.Value, StringComparer.Ordinal)
                .ThenBy(static r => r.Description, StringComparer.Ordinal))
            {
                parts.Add("req");
                parts.Add(requirement.CapabilityCode.Value);
                parts.Add(requirement.Description);
            }

            foreach (var capability in OrderCapabilities(evaluation.Capabilities))
            {
                AppendCapability(parts, "adpCap", capability);
            }

            foreach (var finding in OrderFindings(evaluation.Findings))
            {
                AppendFinding(parts, "adpFind", finding);
            }
        }

        foreach (var finding in OrderFindings(result.BlockingFindings))
        {
            AppendFinding(parts, "find", finding);
        }

        return DeterministicHash.Compute(parts);
    }

    private static string Bit(bool? value) => value == true ? "1" : "0";

    private static IEnumerable<EvCapability> OrderCapabilities(IEnumerable<EvCapability> capabilities) =>
        capabilities
            .OrderBy(static c => c.CapabilityCode.Value, StringComparer.Ordinal)
            .ThenBy(static c => c.CapabilityVersion)
            .ThenBy(static c => c.Availability.ToString(), StringComparer.Ordinal);

    private static IEnumerable<EvDiscoveryFinding> OrderFindings(IEnumerable<EvDiscoveryFinding> findings) =>
        findings
            .OrderBy(static f => f.ResultCode.Value, StringComparer.Ordinal)
            .ThenBy(static f => f.CapabilityCode?.Value ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static f => f.Reason, StringComparer.Ordinal)
            .ThenBy(static f => f.ErrorCategory.ToString(), StringComparer.Ordinal);

    private static void AppendCapability(List<string> parts, string tag, EvCapability capability)
    {
        parts.Add(tag);
        parts.Add(capability.CapabilityCode.Value);
        parts.Add(capability.CapabilityVersion.ToString(CultureInfo.InvariantCulture));
        parts.Add(capability.Availability.ToString());
        parts.Add(capability.EvidenceReference);
        parts.Add(capability.BlockingReason ?? string.Empty);
    }

    private static void AppendFinding(List<string> parts, string tag, EvDiscoveryFinding finding)
    {
        parts.Add(tag);
        parts.Add(finding.ResultCode.Value);
        parts.Add(finding.CapabilityCode?.Value ?? string.Empty);
        parts.Add(finding.Reason);
        parts.Add(finding.ErrorCategory.ToString());
    }
}
