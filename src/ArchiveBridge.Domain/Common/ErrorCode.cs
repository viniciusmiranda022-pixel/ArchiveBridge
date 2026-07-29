namespace ArchiveBridge.Domain.Common;

/// <summary>Taxonomia mínima de erros (runbook §9.1). Scaffolding.</summary>
public enum ErrorCode
{
    Validation,
    Authorization,
    CapabilityBlocked,
    TransientProvider,
    PermanentProvider,
    ConcurrencyLost,
    AmbiguousExternalResult,
    ArtifactIntegrity,
    SecurityPolicy,
    ResourceExhaustion,
    OperatorActionRequired,
}
