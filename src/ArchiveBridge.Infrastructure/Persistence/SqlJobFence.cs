using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Persistence;

/// <summary>
/// Cercamento (fencing) dos EFEITOS de uma operação durável, aplicado na MESMA transação da gravação.
/// O fragmento <see cref="GuardSql"/> valida que o Job continua em Processing, é do mesmo worker e da
/// mesma <c>lease_epoch</c> — o TOKEN DE CERCAMENTO (ver 0001: a época incrementa a cada
/// claim/reclaim). Se o lease expirou e foi recuperado pelo reaper e/ou reivindicado por outra época,
/// a época e/ou o estado divergem e a guarda lança <c>50030</c>, abortando a gravação (rollback) —
/// nenhum dono defasado persiste efeito parcial. Um lease expirado ainda NÃO reivindicado mantém a
/// mesma época/estado e o dono continua sendo o único legítimo (escrita segura). Quando não há
/// cercamento (invocação não durável), os parâmetros são nulos e a guarda é inerte
/// (<c>@fenceJob IS NULL</c>). Os nomes usam o prefixo <c>@fence</c> para não colidir com os
/// parâmetros do UPDATE/INSERT.
/// </summary>
internal static class SqlJobFence
{
    /// <summary>Código do erro de cercamento perdido.</summary>
    public const int FenceError = 50030;

    /// <summary>Fragmento SQL da guarda (prefixe-o ao UPDATE/INSERT dos efeitos, na mesma transação).</summary>
    public const string GuardSql =
        """
        IF @fenceJob IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM dbo.jobs
            WHERE job_id = @fenceJob AND tenant_id = @fenceTenant AND project_id = @fenceProject
              AND owner_worker = @fenceWorker AND lease_epoch = @fenceEpoch AND state = 1)
            THROW 50030, 'Job fora do cercamento (época/dono divergente ou não mais Processing); efeito recusado.', 1;
        """;

    /// <summary>Adiciona os parâmetros do cercamento (nulos quando <paramref name="fence"/> é nulo).</summary>
    public static void Bind(SqlCommand command, JobFence? fence)
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
    }

    /// <summary>
    /// Executa o comando traduzindo os erros de cercamento (<c>50030</c> ⇒ <see cref="FencedOutException"/>)
    /// e de concorrência otimista (<paramref name="concurrencyError"/> ⇒ <see cref="ConcurrencyException"/>).
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
                "Efeito recusado: o cercamento do Job foi perdido (lease expirado/época divergente).", exception);
        }
        catch (SqlException exception) when (exception.Number == concurrencyError)
        {
            throw new ConcurrencyException(
                $"{aggregateLabel} alterado concorrentemente; releia o estado atual antes de gravar.", exception);
        }
    }
}
