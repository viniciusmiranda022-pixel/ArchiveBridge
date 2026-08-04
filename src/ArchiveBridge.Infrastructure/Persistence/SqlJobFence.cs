using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Persistence;

/// <summary>
/// Cercamento (fencing) ATÔMICO dos EFEITOS de uma operação durável. O fragmento <see cref="GuardSql"/>
/// valida, na transação da gravação, que o Job continua em Processing, é do mesmo worker e época, e com
/// o lease AINDA VÁLIDO (<c>lease_expires_at_utc &gt; @fenceNow</c>) — e adquire um lock
/// <c>UPDLOCK, HOLDLOCK</c> sobre a linha do Job que é RETIDO até o commit, bloqueando o reaper (que usa
/// UPDATE simples) entre a guarda e o efeito. Antes do commit, <see cref="RevalidateAsync"/> reconfere
/// o cercamento com um instante FRESCO (recusa lease que expirou durante a operação). Quando não há
/// cercamento (invocação não durável), os parâmetros são nulos e a guarda é inerte. Um lease expirado —
/// mesmo ainda não recuperado — não persiste efeito.
/// </summary>
internal static class SqlJobFence
{
    /// <summary>Código do erro de cercamento perdido/lease inválido.</summary>
    public const int FenceError = 50030;

    /// <summary>Guarda com lock retido até o commit + lease válido (prefixe-a ao efeito, na MESMA transação).</summary>
    public const string GuardSql =
        """
        IF @fenceJob IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM dbo.jobs WITH (UPDLOCK, HOLDLOCK)
            WHERE job_id = @fenceJob AND tenant_id = @fenceTenant AND project_id = @fenceProject
              AND owner_worker = @fenceWorker AND lease_epoch = @fenceEpoch AND state = 1
              AND lease_expires_at_utc IS NOT NULL AND lease_expires_at_utc > @fenceNow)
            THROW 50030, 'Job fora do cercamento (época/dono divergente, não Processing ou lease expirado); efeito recusado.', 1;
        """;

    /// <summary>Adiciona os parâmetros do cercamento (nulos quando <paramref name="fence"/> é nulo).</summary>
    public static void Bind(SqlCommand command, JobFence? fence, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Parameters.Add(new SqlParameter("@fenceJob", SqlDbType.UniqueIdentifier)
        { Value = fence is { } job ? job.Job.Value : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@fenceTenant", SqlDbType.UniqueIdentifier)
        { Value = fence is { } tenant ? tenant.Scope.Tenant.Value : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@fenceProject", SqlDbType.UniqueIdentifier)
        { Value = fence is { } project ? project.Scope.Project.Value : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@fenceWorker", SqlDbType.NVarChar, 200)
        { Value = fence is { } worker ? worker.Worker.Value : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@fenceEpoch", SqlDbType.BigInt)
        { Value = fence is { } epoch ? epoch.Epoch.Value : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@fenceNow", SqlDbType.DateTime2) { Value = nowUtc });
    }

    /// <summary>
    /// Executa o comando (guarda + efeito na mesma transação) traduzindo os erros de cercamento
    /// (<c>50030</c> ⇒ <see cref="FencedOutException"/>) e de concorrência otimista
    /// (<paramref name="concurrencyError"/> ⇒ <see cref="ConcurrencyException"/>).
    /// </summary>
    public static async Task ExecuteGuardedAsync(
        SqlCommand command, int concurrencyError, string aggregateLabel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == FenceError)
        {
            throw new FencedOutException(
                "Efeito recusado: cercamento do Job perdido (época/dono/lease inválido).", exception);
        }
        catch (SqlException exception) when (exception.Number == concurrencyError)
        {
            throw new ConcurrencyException(
                $"{aggregateLabel} alterado concorrentemente; releia o estado atual antes de gravar.", exception);
        }
    }

    /// <summary>
    /// Revalida o cercamento imediatamente antes do commit, com um instante FRESCO
    /// (<paramref name="nowUtc"/>): o lock já está retido, então época/dono não mudaram, mas um lease
    /// que expirou DURANTE a operação é recusado (fail-closed). Inerte quando não há cercamento.
    /// </summary>
    public static async Task RevalidateAsync(
        SqlConnection connection, SqlTransaction transaction, JobFence? fence, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (fence is null)
        {
            return;
        }

        await using var command = new SqlCommand($"SET NOCOUNT ON;\n{GuardSql}", connection, transaction);
        Bind(command, fence, nowUtc);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == FenceError)
        {
            throw new FencedOutException(
                "Efeito recusado na revalidação: o lease expirou durante a operação.", exception);
        }
    }
}
