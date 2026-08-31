namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>Entrada FIXA do catálogo — nunca construída fora de <see cref="MigrationCompletionCriterionCatalog"/>.</summary>
public sealed record MigrationCompletionCriterionDefinition
{
    internal MigrationCompletionCriterionDefinition(
        MigrationCompletionCriterionId id, MigrationCompletionCriterionEvidenceSource evidenceSource, string description)
    {
        Id = id;
        EvidenceSource = evidenceSource;
        Description = description;
    }

    /// <summary>Identidade estável do critério.</summary>
    public MigrationCompletionCriterionId Id { get; }

    /// <summary>Como este critério é/pode ser resolvido — nunca sobrescrevível por um chamador.</summary>
    public MigrationCompletionCriterionEvidenceSource EvidenceSource { get; }

    /// <summary>Texto descritivo curto (referência ao item literal do runbook §49) — nunca segredo/PII.</summary>
    public string Description { get; }
}
