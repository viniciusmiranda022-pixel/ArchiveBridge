namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>Entrada FIXA do catálogo — nunca construída fora de <see cref="ReadinessControlCatalog"/>.</summary>
public sealed record ReadinessControlDefinition
{
    internal ReadinessControlDefinition(
        ReadinessControlId id, ReadinessGateGroup group, ReadinessControlEvidenceSource evidenceSource, string description)
    {
        Id = id;
        Group = group;
        EvidenceSource = evidenceSource;
        Description = description;
    }

    /// <summary>Identidade estável do controle.</summary>
    public ReadinessControlId Id { get; }

    /// <summary>Grupo de gate (§47.1-§47.5).</summary>
    public ReadinessGateGroup Group { get; }

    /// <summary>Como este controle é/pode ser resolvido — nunca sobrescrevível por um chamador.</summary>
    public ReadinessControlEvidenceSource EvidenceSource { get; }

    /// <summary>Texto descritivo curto (referência ao item do runbook §47) — nunca segredo/PII.</summary>
    public string Description { get; }
}
