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
/// Impressão digital SEMÂNTICA COMPLETA de uma execução de descoberta: identidade integral do ambiente,
/// versão observada/produto/fonte, todas as capacidades, a assinatura normalizada e seu hash, as
/// avaliações de adapter (compatibilidade/precedência/perfil), o adapter selecionado, os achados, o
/// código de resultado, o status e a versão de esquema. Não depende apenas da disponibilidade das
/// capacidades — qualquer diferença semântica muda o hash.
/// </summary>
public static class EvDiscoverySemanticFingerprint
{
    /// <summary>Computa o hash semântico completo da execução.</summary>
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

        foreach (var capability in result.CapabilitySet.Capabilities.OrderBy(static c => c.CapabilityCode.Value, StringComparer.Ordinal))
        {
            parts.Add("cap");
            parts.Add(capability.CapabilityCode.Value);
            parts.Add(capability.CapabilityVersion.ToString(CultureInfo.InvariantCulture));
            parts.Add(capability.Availability.ToString());
            parts.Add(capability.EvidenceReference);
            parts.Add(capability.BlockingReason ?? string.Empty);
        }

        parts.Add("sig");
        parts.Add(result.Signature?.SignatureHash.Value ?? "<none>");
        parts.Add(result.Signature is { } signature ? string.Join(",", signature.Parameters) : string.Empty);

        foreach (var evaluation in result.Selection.Evaluations.OrderBy(static e => e.AdapterId.Value, StringComparer.Ordinal))
        {
            parts.Add("adp");
            parts.Add(evaluation.AdapterId.Value);
            parts.Add(evaluation.AdapterVersion.ToString(CultureInfo.InvariantCulture));
            parts.Add(evaluation.Compatibility.ToString());
            parts.Add(evaluation.Precedence.ToString(CultureInfo.InvariantCulture));
            parts.Add(evaluation.ProfileId ?? string.Empty);
        }

        foreach (var finding in result.BlockingFindings)
        {
            parts.Add("find");
            parts.Add(finding.ResultCode.Value);
            parts.Add(finding.CapabilityCode?.Value ?? string.Empty);
            parts.Add(finding.Reason);
        }

        return DeterministicHash.Compute(parts);
    }
}
