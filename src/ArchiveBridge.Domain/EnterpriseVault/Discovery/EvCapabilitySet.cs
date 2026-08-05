using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Conjunto versionado de capacidades descobertas para um ambiente, ligado ao adapter selecionado (se
/// houver) e ao estado da avaliação. Carrega dois hashes determinísticos: <see cref="ConfigurationHash"/>
/// (o que ENQUADROU a descoberta — ambiente, adapter, esquema) e <see cref="EvidenceHash"/> (a EVIDÊNCIA
/// observada — cada capacidade com sua disponibilidade/versão/evidência). Alterar um único byte de
/// qualquer capacidade muda o <see cref="EvidenceHash"/>. Uma nova descoberta cria um novo conjunto; a
/// anterior só é marcada <see cref="EvDiscoveryStatus.Superseded"/> após a nova estar completa.
/// </summary>
public sealed record EvCapabilitySet(
    EvEnvironmentId EnvironmentId,
    EvAdapterId? AdapterId,
    int? AdapterVersion,
    int DiscoverySchemaVersion,
    IReadOnlyList<EvCapability> Capabilities,
    Sha256Hash ConfigurationHash,
    Sha256Hash EvidenceHash,
    EvDiscoveryStatus Status)
{
    /// <summary>
    /// Cria um conjunto de capacidades computando os hashes determinísticos de configuração e evidência a
    /// partir das entradas (as capacidades são ordenadas por código para o hash ser estável).
    /// </summary>
    public static EvCapabilitySet Create(
        EvEnvironmentId environmentId,
        EvAdapterId? adapterId,
        int? adapterVersion,
        int discoverySchemaVersion,
        IReadOnlyList<EvCapability> capabilities,
        EvDiscoveryStatus status)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        // Invariante de unicidade (alinhada a UQ_evcap_code): recusa códigos repetidos, fail-closed.
        EvDiscoveryInvariants.EnsureUniqueCapabilities(capabilities, "conjunto de capacidades");
        var ordered = capabilities
            .OrderBy(static capability => capability.CapabilityCode.Value, StringComparer.Ordinal)
            .ToArray();

        var configurationHash = DeterministicHash.Compute(
        [
            "env", environmentId.Value.ToString("N"),
            "adapter", adapterId?.Value ?? "<none>",
            "adapterVersion", (adapterVersion ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "schema", discoverySchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);

        var evidenceParts = new List<string> { "schema", discoverySchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var capability in ordered)
        {
            evidenceParts.Add(capability.CapabilityCode.Value);
            evidenceParts.Add(capability.CapabilityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            evidenceParts.Add(capability.Availability.ToString());
            evidenceParts.Add(capability.EvidenceReference);
            evidenceParts.Add(capability.BlockingReason ?? string.Empty);
        }

        return new EvCapabilitySet(
            environmentId,
            adapterId,
            adapterVersion,
            discoverySchemaVersion,
            ordered,
            configurationHash,
            DeterministicHash.Compute(evidenceParts),
            status);
    }

    /// <summary>Disponibilidade descoberta de uma capacidade (Indeterminate se ausente do conjunto — fail-closed).</summary>
    public CapabilityAvailability AvailabilityOf(string capabilityCode)
    {
        foreach (var capability in Capabilities)
        {
            if (string.Equals(capability.CapabilityCode.Value, capabilityCode, StringComparison.Ordinal))
            {
                return capability.Availability;
            }
        }

        return CapabilityAvailability.Indeterminate;
    }

    /// <summary>Indica se a capacidade indicada foi comprovada como disponível (evidência positiva).</summary>
    public bool IsAvailable(string capabilityCode) => AvailabilityOf(capabilityCode) == CapabilityAvailability.Available;

    /// <summary>
    /// Marca o conjunto como substituído por uma descoberta posterior (transição fail-closed): só de um
    /// estado terminal para <see cref="EvDiscoveryStatus.Superseded"/>.
    /// </summary>
    public EvCapabilitySet Supersede()
    {
        EvDiscoveryTransitions.EnsureCanTransition(Status, EvDiscoveryStatus.Superseded);
        return this with { Status = EvDiscoveryStatus.Superseded };
    }
}
