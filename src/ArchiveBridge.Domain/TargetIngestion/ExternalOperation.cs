namespace ArchiveBridge.Domain.TargetIngestion;

/// <summary>Chave determinística da operação externa, gerada antes do efeito (ADR-0006).</summary>
public readonly record struct OperationKey(string Value);

/// <summary>Estados do ledger external_operations (ADR-0003). Ambíguo nunca repete automaticamente.</summary>
public enum ExternalOperationState
{
    Intent,
    Submitted,
    Confirmed,
    Ambiguous,
    Failed,
}

/// <summary>Estado de capability de uma rota/adapter de destino (catálogo ADR-0006/0007).</summary>
public enum CapabilityState
{
    BlockedPendingEvidence,
    Candidate,
    Certified,
    Enabled,
    Disabled,
}

/// <summary>Operação externa (skeleton). Sem transação distribuída com o serviço Microsoft.</summary>
public sealed class ExternalOperation(OperationKey key, ExternalOperationState state)
{
    public OperationKey Key { get; } = key;
    public ExternalOperationState State { get; private set; } = state;
}
