using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Application.Planning;

/// <summary>
/// Desfecho do batimento periódico: se o cercamento foi PERDIDO durante a execução e quantas
/// renovações efetivas ocorreram. Uma renovação perdida (época/dono defasado, lease vencido ou
/// exceção transitória) é fail-closed: cancela a operação e marca <see cref="Lost"/>.
/// </summary>
public sealed record HeartbeatResult(bool Lost, int Renewals, JobCommandOutcome LastOutcome);

/// <summary>
/// Batimento (heartbeat) PERIÓDICO real de um comando durável. Renova o lease do Job repetidamente
/// enquanto a operação está em curso — não uma única vez antes dela. O laço de batimento roda numa
/// <see cref="CancellationTokenSource"/> VINCULADA ao token da operação, com intervalo menor que o
/// lease (tipicamente <c>lease / 3</c>): assim uma operação mais longa que o lease permanece válida.
/// A perda do cercamento (<see cref="JobCommandOutcome.FencedOut"/>/<see cref="JobCommandOutcome.NotFound"/>)
/// ou uma exceção transitória na renovação CANCELA imediatamente a operação (nenhum efeito novo é
/// iniciado) e marca <see cref="HeartbeatResult.Lost"/>. Ao término da operação (sucesso ou falha) o
/// batimento para de imediato e é aguardado no <c>finally</c> — nunca fica uma Task órfã renovando um
/// lease de um comando já encerrado.
/// </summary>
public sealed class PlanningHeartbeat(IJobLeaseManager leases)
{
    private readonly IJobLeaseManager _leases = leases;

    /// <summary>
    /// Executa <paramref name="operation"/> enquanto renova o lease a cada <paramref name="interval"/>.
    /// Se o batimento perde o cercamento, cancela o token da operação e devolve <c>Lost = true</c>; se a
    /// própria operação lança (erro de domínio, concorrência, <see cref="FencedOutException"/> na
    /// gravação cercada), a exceção PROPAGA — o batimento apenas para e é aguardado.
    /// </summary>
    public async Task<HeartbeatResult> RunWhileAsync(
        LeaseCommand lease, TimeSpan interval, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(operation);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new HeartbeatState();
        var beat = BeatLoopAsync(lease, interval, cts, state);
        try
        {
            await operation(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state.Lost && !cancellationToken.IsCancellationRequested)
        {
            // Cancelamento causado pela PERDA do cercamento (não pelo chamador): o desfecho é Fenced,
            // reportado via HeartbeatResult.Lost — não é uma exceção a propagar.
        }
        finally
        {
            cts.Cancel();                       // encerra o batimento assim que a operação termina
            await beat.ConfigureAwait(false);   // aguarda o laço: nenhuma Task de batimento órfã
        }

        return new HeartbeatResult(state.Lost, state.Renewals, state.LastOutcome);
    }

    private async Task BeatLoopAsync(
        LeaseCommand lease, TimeSpan interval, CancellationTokenSource cts, HeartbeatState state)
    {
        var beatToken = cts.Token;
        while (!beatToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, beatToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // a operação terminou (ou o chamador cancelou) → para o batimento
            }

            JobCommandOutcome outcome;
            try
            {
                outcome = await _leases.RenewAsync(lease, beatToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // cancelado durante a renovação: a operação terminou
            }
            catch
            {
                // Exceção transitória no batimento: FAIL-CLOSED — cancela a operação e marca perdido.
                // Não se pode provar que o lease continua válido, logo nenhum efeito novo deve seguir.
                state.Lost = true;
                await cts.CancelAsync().ConfigureAwait(false);
                return;
            }

            state.LastOutcome = outcome;
            if (outcome is JobCommandOutcome.Applied or JobCommandOutcome.IdempotentReplay)
            {
                state.Renewals++;
                continue;
            }

            // FencedOut/NotFound: cercamento perdido — cancela a operação; nenhum efeito novo é iniciado.
            state.Lost = true;
            await cts.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    private sealed class HeartbeatState
    {
        public bool Lost;
        public int Renewals;
        public JobCommandOutcome LastOutcome = JobCommandOutcome.Applied;
    }
}
