namespace ArchiveBridge.Domain.Security;

/// <summary>Desfecho de <see cref="WdacPolicyEvidence.Validate"/> contra um <see cref="WdacCandidateBinary"/>.</summary>
public enum WdacValidationOutcome
{
    /// <summary>O candidato corresponde a uma entrada da allowlist (por hash, ou por publisher + path rule específica).</summary>
    Allowed,

    /// <summary>Nenhuma entrada da allowlist corresponde ao candidato — fail-closed default.</summary>
    Denied,
}
