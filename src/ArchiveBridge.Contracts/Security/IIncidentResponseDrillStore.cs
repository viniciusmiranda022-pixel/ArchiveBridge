using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Contracts.Security;

/// <summary>Porta de persistência do <see cref="IncidentResponseDrillRecord"/> (AB-I7-008). Append-only, versionado por (tenant, project, tipo de drill).</summary>
public interface IIncidentResponseDrillStore
{
    /// <summary>Aloca a próxima versão do drill sob lock — ou converge idempotentemente quando o resultado é o mesmo.</summary>
    Task<IncidentResponseDrillRecord> RecordDrillAsync(
        TenantScope scope,
        IncidentResponseDrillType drillType,
        IncidentResponseDrillOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        Sha256Hash evidenceDigest,
        string disposition,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>O registro VIGENTE deste escopo/tipo de drill — <see langword="null"/> se nunca exercitado. Revalida integridade fail-closed.</summary>
    Task<IncidentResponseDrillRecord?> GetLatestAsync(TenantScope scope, IncidentResponseDrillType drillType, CancellationToken cancellationToken);
}
