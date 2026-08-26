using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>
/// Resultado de <see cref="JobRetryGate.ScheduleRetryOrFailAsync"/>: além do
/// <see cref="JobCommandOutcome"/> bruto da escrita, informa qual transição foi de fato TENTADA
/// (RetryScheduled vs. Failed) — necessário porque <see cref="JobCommandOutcome"/> sozinho (Applied/
/// IdempotentReplay/FencedOut/NotFound) não distingue o estado alvo. Sem isso, um chamador não teria
/// como saber se um orçamento esgotado converteu silenciosamente o pedido de retry em falha terminal.
/// </summary>
public readonly record struct JobRetryGateResult(bool RetryScheduled, JobCommandOutcome Outcome);

/// <summary>
/// Único ponto de decisão do ORÇAMENTO de retry para falhas ATIVAS reportadas por um processador de
/// comando (AB-I7-002). Distinto do reaper de lease expirado (<c>SqlJobLeaseManager</c>), que já aplica
/// <see cref="RetryPolicy"/>/<c>AttemptCount</c> ao recuperar um lease vencido: antes deste gate, os
/// processadores de comando (Purview upload, EV export/discovery, Planning) chamavam
/// <see cref="IJobStore.ScheduleRetryAsync"/> diretamente para toda falha ATIVA, sem NUNCA consultar o
/// orçamento — um Job cuja causa de falha nunca se resolvesse (ex.: SAS permanentemente consumido)
/// podia oscilar indefinidamente entre Processing/RetryScheduled sem jamais convergir a um estado
/// terminal. Este gate fecha essa lacuna sendo o ÚNICO caminho pelo qual um processador agenda retry
/// automático após falha ativa: consulta a MESMA <see cref="RetryPolicy"/>/contagem de tentativas já
/// persistida (a fonte de verdade em SQL) e só agenda nova tentativa enquanto houver orçamento —
/// caso contrário converge atomicamente para <c>Failed</c> com <see cref="ErrorCode.ResourceExhaustion"/>
/// (o MESMO código estável já usado pelo reaper ao esgotar tentativas), nunca reentrando em
/// RetryScheduled. A escrita em si permanece sob fencing (owner_worker + lease_epoch): um dono
/// defasado nunca agenda retry nem consome orçamento — a leitura do orçamento é só consultiva, a
/// escrita cercada é quem decide.
/// </summary>
public static class JobRetryGate
{
    /// <summary>
    /// Agenda nova tentativa (Processing → RetryScheduled) enquanto <paramref name="retryPolicy"/> ainda
    /// permitir, a partir da contagem de tentativas persistida; caso contrário falha terminalmente
    /// (Processing → Failed) com <see cref="ErrorCode.ResourceExhaustion"/>. Sob fencing por época —
    /// owner/epoch defasado nunca aplica nenhuma das duas transições.
    /// </summary>
    public static async Task<JobRetryGateResult> ScheduleRetryOrFailAsync(
        IJobStore store,
        IClock clock,
        RetryPolicy retryPolicy,
        LeaseCommand lease,
        ErrorCode transientError,
        TimeSpan backoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lease);

        var snapshot = await store.GetAsync(lease.Scope, lease.JobId, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null && retryPolicy.ShouldRetry(snapshot.AttemptCount))
        {
            var outcome = await store
                .ScheduleRetryAsync(lease, transientError, clock.UtcNow + backoff, cancellationToken)
                .ConfigureAwait(false);
            return new JobRetryGateResult(RetryScheduled: true, outcome);
        }

        var failed = await store.FailAsync(lease, ErrorCode.ResourceExhaustion, cancellationToken).ConfigureAwait(false);
        return new JobRetryGateResult(RetryScheduled: false, failed);
    }
}
