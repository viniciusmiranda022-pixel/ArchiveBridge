using System.Data;
using ArchiveBridge.Contracts.Approvals;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Approvals;

/// <summary>
/// Liga os parâmetros da linha de <c>approvals</c> gravada atomicamente junto da transição do
/// agregado. Usa nomes prefixados por <c>@ap</c> para não colidir com os parâmetros do UPDATE.
/// </summary>
internal static class SqlApprovalCommand
{
    /// <summary>Fragmento INSERT (mesmos nomes de parâmetro de <see cref="Bind"/>).</summary>
    public const string InsertSql =
        """
        INSERT INTO dbo.approvals
            (tenant_id, project_id, subject_type, subject_id, subject_version, decision,
             approved_by, approved_at_utc, correlation_id, notes)
        VALUES
            (@apTenant, @apProject, @apSubjectType, @apSubjectId, @apSubjectVersion, @apDecision,
             @apApprovedBy, @apApprovedAt, @apCorrelation, @apNotes);
        """;

    /// <summary>Adiciona os parâmetros da decisão ao comando.</summary>
    public static void Bind(SqlCommand command, Guid tenant, Guid owningProject, ApprovalRecord approval)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(approval);
        command.Parameters.Add(new SqlParameter("@apTenant", SqlDbType.UniqueIdentifier) { Value = tenant });
        command.Parameters.Add(new SqlParameter("@apProject", SqlDbType.UniqueIdentifier) { Value = owningProject });
        command.Parameters.Add(new SqlParameter("@apSubjectType", SqlDbType.TinyInt) { Value = (byte)approval.SubjectType });
        command.Parameters.Add(new SqlParameter("@apSubjectId", SqlDbType.UniqueIdentifier) { Value = approval.SubjectId });
        command.Parameters.Add(new SqlParameter("@apSubjectVersion", SqlDbType.Int) { Value = approval.SubjectVersion });
        command.Parameters.Add(new SqlParameter("@apDecision", SqlDbType.TinyInt) { Value = (byte)approval.Decision });
        command.Parameters.Add(new SqlParameter("@apApprovedBy", SqlDbType.NVarChar, 200) { Value = approval.ApprovedBy });
        command.Parameters.Add(new SqlParameter("@apApprovedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(approval.ApprovedAtUtc) });
        command.Parameters.Add(new SqlParameter("@apCorrelation", SqlDbType.UniqueIdentifier) { Value = approval.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@apNotes", SqlDbType.NVarChar, 400) { Value = (object?)approval.Notes ?? DBNull.Value });
    }
}
