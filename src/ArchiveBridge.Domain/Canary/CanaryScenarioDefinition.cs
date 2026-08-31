namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Definição FIXA de UM cenário do catálogo do canário (AB-I8-004) — nunca construída fora de
/// <see cref="CanaryScenarioCatalog"/>.
/// </summary>
public sealed record CanaryScenarioDefinition
{
    internal CanaryScenarioDefinition(CanaryScenarioId id, CanaryScenarioEvidenceSource evidenceSource, string description)
    {
        Id = id;
        EvidenceSource = evidenceSource;
        Description = description;
    }

    /// <summary>Identidade estável do cenário.</summary>
    public CanaryScenarioId Id { get; }

    /// <summary>Como este cenário pode ser resolvido — nunca alterável pelo chamador.</summary>
    public CanaryScenarioEvidenceSource EvidenceSource { get; }

    /// <summary>Descrição curta, alinhada ao texto literal do runbook §48.</summary>
    public string Description { get; }
}
