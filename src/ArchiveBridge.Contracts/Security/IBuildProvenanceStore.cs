using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Contracts.Security;

/// <summary>Porta de persistência do <see cref="BuildProvenanceRecord"/> (AB-I7-008). Append-only, versionado por (tenant, project, artifact).</summary>
public interface IBuildProvenanceStore
{
    /// <summary>Aloca a próxima versão da build aprovada sob lock — ou converge idempotentemente quando o conteúdo é o mesmo.</summary>
    Task<BuildProvenanceRecord> ApproveAsync(
        TenantScope scope,
        string artifactName,
        string sourceCommitSha,
        string builderIdentity,
        DateTimeOffset buildTimestampUtc,
        Sha256Hash artifactDigest,
        string approvedBy,
        string approvedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>A build APROVADA vigente deste artifact — <see langword="null"/> se nenhuma aprovada ainda. Revalida integridade fail-closed.</summary>
    Task<BuildProvenanceRecord?> GetLatestAsync(TenantScope scope, string artifactName, CancellationToken cancellationToken);
}
