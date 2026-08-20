using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Custódia SQL dos artefatos PST (<see cref="MigrationArtifact"/>). Append-only, tenant/projeto scoped
/// (RLS + filtro explícito por <c>project_id</c>), identidade da aplicação (nunca a de manutenção).
/// <see cref="FindAsync"/> nunca distingue "não existe" de "pertence a outro tenant/projeto" — o filtro por
/// <c>project_id</c> garante que um id de outro escopo simplesmente não é encontrado (anti-IDOR).
/// </summary>
public sealed class SqlPstCustodyStore(TenantConnectionFactory connectionFactory, IClock clock) : IPstCustodyStore
{
    private const string FindSql =
        """
        SET NOCOUNT ON;
        SELECT artifact_id, tenant_id, project_id, relative_path, registered_hash, registered_size_bytes, registered_at_utc
        FROM dbo.pst_artifacts
        WHERE artifact_id = @artifact AND project_id = @project;
        """;

    private const string InsertSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.pst_artifacts
            (artifact_id, tenant_id, project_id, relative_path, registered_hash, registered_size_bytes, registered_at_utc)
        VALUES
            (@artifact, @tenant, @project, @relativePath, @hash, @sizeBytes, @registeredAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<MigrationArtifact?> FindAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(FindSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = artifact.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MigrationArtifact.Rehydrate(
            new ArtifactId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new PstRelativePath(reader.GetString(3)),
            new Sha256Hash(reader.GetString(4).TrimEnd()),
            reader.GetInt64(5),
            SqlJobMapping.ReadUtc(reader.GetDateTime(6)));
    }

    /// <inheritdoc />
    public async Task<MigrationArtifact> RegisterAsync(
        TenantId tenant,
        ProjectId project,
        PstRelativePath relativePath,
        Sha256Hash observedHash,
        long observedSizeBytes,
        CancellationToken cancellationToken)
    {
        var registeredAtUtc = _clock.UtcNow;
        var artifact = MigrationArtifact.Register(tenant, project, relativePath, observedHash, observedSizeBytes, registeredAtUtc);

        var scope = new TenantScope(tenant, project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = artifact.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = project.Value });
        command.Parameters.Add(new SqlParameter("@relativePath", SqlDbType.NVarChar, 400) { Value = relativePath.Value });
        command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = observedHash.Value });
        command.Parameters.Add(new SqlParameter("@sizeBytes", SqlDbType.BigInt) { Value = observedSizeBytes });
        command.Parameters.Add(new SqlParameter("@registeredAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(registeredAtUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return artifact;
    }
}
