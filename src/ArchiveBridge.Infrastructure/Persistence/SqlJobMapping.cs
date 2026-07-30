using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Persistence;

/// <summary>Conversões entre o modelo de domínio e as colunas SQL (enums em TINYINT, UTC em DATETIME2).</summary>
internal static class SqlJobMapping
{
    /// <summary>Colunas do Job na ordem lida por <see cref="ReadSnapshot"/>.</summary>
    public const string JobColumns =
        "job_id, tenant_id, project_id, workload, state, priority, attempt_count, owner_worker, " +
        "lease_epoch, lease_expires_at_utc, next_attempt_at_utc, last_error_code, created_at_utc, updated_at_utc";

    /// <summary>Converte um instante para o valor armazenado (DATETIME2 em UTC).</summary>
    public static DateTime ToDbUtc(DateTimeOffset value) => value.UtcDateTime;

    /// <summary>Lê um DATETIME2 como instante UTC.</summary>
    public static DateTimeOffset ReadUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Materializa um <see cref="JobSnapshot"/> na ordem de <see cref="JobColumns"/>.</summary>
    public static JobSnapshot ReadSnapshot(SqlDataReader reader)
    {
        var id = new JobId(reader.GetGuid(0));
        var tenant = new TenantId(reader.GetGuid(1));
        var project = new ProjectId(reader.GetGuid(2));
        var workload = (Workload)reader.GetByte(3);
        var state = (JobState)reader.GetByte(4);
        var priority = new JobPriority(reader.GetInt32(5));
        var attemptCount = reader.GetInt32(6);
        WorkerId? owner = reader.IsDBNull(7) ? null : new WorkerId(reader.GetString(7));
        var epoch = new LeaseEpoch(reader.GetInt64(8));
        DateTimeOffset? leaseExpires = reader.IsDBNull(9) ? null : ReadUtc(reader.GetDateTime(9));
        DateTimeOffset? nextAttempt = reader.IsDBNull(10) ? null : ReadUtc(reader.GetDateTime(10));
        ErrorCode? lastError = reader.IsDBNull(11) ? null : (ErrorCode)reader.GetByte(11);
        var createdAt = ReadUtc(reader.GetDateTime(12));
        var updatedAt = ReadUtc(reader.GetDateTime(13));

        return new JobSnapshot(
            id,
            tenant,
            project,
            workload,
            priority,
            state,
            owner,
            epoch,
            leaseExpires,
            attemptCount,
            nextAttempt,
            lastError,
            createdAt,
            updatedAt);
    }
}
