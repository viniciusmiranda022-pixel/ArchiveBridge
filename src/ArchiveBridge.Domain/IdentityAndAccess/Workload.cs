namespace ArchiveBridge.Domain.IdentityAndAccess;

/// <summary>Workloads isolados por identidade própria (ADR-0008). Scaffolding.</summary>
public enum Workload
{
    Control,
    EnterpriseVault,
    Pst,
    Upload,
    Reconciliation,
    Evidence,
}
