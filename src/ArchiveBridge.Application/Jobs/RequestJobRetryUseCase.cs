using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>
/// Solicitação de retry manual autorizado vinda do Portal (Passo 7). O cliente informa APENAS o escopo
/// autenticado, o <see cref="JobId"/>-alvo (entrada não confiável, re-resolvida dentro do escopo), a chave
/// de idempotência e a correlação. Tenant/projeto NUNCA vêm do cliente como autoridade — chegam apenas
/// através de <see cref="TenantScope"/>, já resolvido server-side.
/// </summary>
public sealed record RequestJobRetry(TenantScope Scope, JobId JobId, Guid IdempotencyKey, CorrelationId Correlation);

/// <summary>
/// Caso de uso de SOLICITAÇÃO de retry manual autorizado de um Job. NÃO faz UPDATE direto de estado nem
/// contorna a fila durável: delega integralmente a <see cref="IJobStore.RequestManualRetryAsync"/>, que é a
/// ÚNICA fronteira que decide elegibilidade (somente <see cref="JobState.RetryScheduled"/>; NUNCA ressuscita
/// um Job concluído ou em estado terminal), aplica idempotência e grava a auditoria da transição. Este caso
/// de uso é independente de ASP.NET — a autorização RBAC/antiforgery/anti-IDOR é responsabilidade da camada
/// Portal, que chama isto SOMENTE após autorizar o principal.
/// </summary>
public sealed class RequestJobRetryUseCase(IJobStore store)
{
    private readonly IJobStore _store = store;

    /// <summary>Solicita o retry manual; resultado explícito (Applied/IdempotentReplay/NotEligible/NotFound/IdempotencyConflict).</summary>
    public Task<JobRetryRequestOutcome> ExecuteAsync(RequestJobRetry request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(request));
        }

        return _store.RequestManualRetryAsync(
            request.Scope, request.JobId, request.IdempotencyKey, request.Correlation, cancellationToken);
    }
}
