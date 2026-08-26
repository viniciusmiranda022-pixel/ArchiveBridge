using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.Jobs;

/// <summary>
/// Porta de leitura (AB-I7-005 item 5) que reconstrói, EXCLUSIVAMENTE a partir do estado canônico
/// persistido em <c>dbo.jobs</c> — nunca de uma fila externa, cache ou log — o conjunto de trabalho
/// atualmente elegível para claim. Reutiliza a MESMA definição de elegibilidade já usada por
/// <c>SqlJobStore.TryClaimNextAsync</c> (<c>state IN (Pending, RetryScheduled) AND (NextAttemptAtUtc IS NULL
/// OR NextAttemptAtUtc &lt;= asOfUtc)</c>) em vez de inventar uma segunda definição.
/// <para>
/// É uma operação de LEITURA PURA: não muda estado, não cerca (fencing), não emite comandos — apenas
/// enumera. Emitir/reivindicar o trabalho listado continua exclusivamente através de
/// <see cref="IJobStore.TryClaimNextAsync"/>, que já é atômico, idempotente e respeita fencing/retry
/// budgets — a reconstrução nunca duplica efeito porque nunca produz efeito algum por si só.
/// </para>
/// </summary>
public interface IPendingWorkRebuildQuery
{
    /// <summary>
    /// Enumera, dentro do escopo tenant/project e workload informados, os Jobs elegíveis para claim no
    /// instante <paramref name="asOfUtc"/> — Jobs em <c>Processing</c> com lease expirado NÃO aparecem aqui
    /// (ainda não são elegíveis: dependem do reaper <see cref="IJobLeaseManager.RecoverExpiredLeasesAsync"/>
    /// rodar primeiro e convergir para <c>RetryScheduled</c>/<c>Failed</c> — a reconstrução nunca ressuscita
    /// um lease diretamente).
    /// </summary>
    Task<IReadOnlyList<JobSnapshot>> RebuildEligibleWorkAsync(
        TenantScope scope, Workload workload, DateTimeOffset asOfUtc, CancellationToken cancellationToken);
}
