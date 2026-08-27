using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Contracts.Security;

/// <summary>Porta de persistência do <see cref="WdacPolicyEvidence"/> (AB-I7-008). Append-only, versionado por (tenant, project).</summary>
public interface IWdacPolicyEvidenceStore
{
    /// <summary>Aloca a próxima versão da policy sob lock — ou converge idempotentemente quando o <see cref="WdacPolicyEvidence.PolicyDigest"/> é o mesmo.</summary>
    Task<WdacPolicyEvidence> RecordPolicyAsync(
        TenantScope scope,
        IReadOnlyList<WdacAllowlistEntry> entries,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>A versão VIGENTE da policy deste escopo — <see langword="null"/> se nenhuma emitida ainda. Revalida integridade fail-closed.</summary>
    Task<WdacPolicyEvidence?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken);
}
