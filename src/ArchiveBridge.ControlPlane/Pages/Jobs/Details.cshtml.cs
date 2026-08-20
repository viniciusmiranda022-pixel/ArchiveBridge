using System.Security.Claims;
using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Jobs;

/// <summary>
/// Job — leitura do estado durável/trilha de transições e, para Operator/Administrator com o gate
/// habilitado, a superfície de RETRY MANUAL AUTORIZADO do Passo 7. O handler NÃO faz UPDATE direto de
/// estado: delega integralmente a <see cref="RequestJobRetryUseCase"/> (que por sua vez delega a
/// <see cref="IJobStore.RequestManualRetryAsync"/>), a ÚNICA fronteira que decide elegibilidade (somente
/// <see cref="JobState.RetryScheduled"/> — NUNCA ressuscita um Job concluído ou em estado terminal),
/// idempotência e auditoria da transição. A oferta do botão na UI é apenas UX; o backend revalida RBAC,
/// escopo, gate, elegibilidade e idempotência independentemente do que a UI mostrou.
/// </summary>
public sealed class DetailsModel(
    IControlPlaneQueries queries,
    IPortalScopeAccessor scopeAccessor,
    IAuthorizationService authorization,
    RequestJobRetryUseCase requestRetry,
    IPortalOperationalAudit operationalAudit,
    JobRetryPortalOptions retryGate,
    Composition.PresentationModeOptions presentation,
    Presentation.IPresentationDataProvider presentationData,
    IClock clock) : PageModel
{
    /// <summary>Código de ação da trilha operacional para a solicitação de retry manual.</summary>
    public const string ActionCode = "jobs.retry.request";
    private const string ResourceType = "job";
    private const string JobRetryOperatorsPolicy = "JobRetryOperators";

    private readonly IControlPlaneQueries _queries = queries;
    private readonly IPortalScopeAccessor _scopeAccessor = scopeAccessor;
    private readonly IAuthorizationService _authorization = authorization;
    private readonly RequestJobRetryUseCase _requestRetry = requestRetry;
    private readonly IPortalOperationalAudit _operationalAudit = operationalAudit;
    private readonly JobRetryPortalOptions _retryGate = retryGate;
    private readonly Composition.PresentationModeOptions _presentation = presentation;
    private readonly Presentation.IPresentationDataProvider _presentationData = presentationData;
    private readonly IClock _clock = clock;

    /// <summary>Identificador do job na query string (entrada do navegador — não confiável).</summary>
    public Guid JobIdValue { get; private set; }

    /// <summary>Resumo do job dentro do escopo autenticado; <see langword="null"/> se inexistente/fora do escopo.</summary>
    public JobSummary? Job { get; private set; }

    /// <summary>Trilha de transições (ordem cronológica); vazia se o job não existe no escopo.</summary>
    public IReadOnlyList<JobTransitionView> Transitions { get; private set; } = [];

    /// <summary>Gate do deployment: o Portal pode aceitar solicitações de retry manual nesta instalação?</summary>
    public bool RetryEnabled { get; private set; }

    /// <summary>
    /// Autorização RBAC (Operator/Administrator) calculada INDEPENDENTEMENTE do estado do gate. Um
    /// principal sem este mandato NUNCA deve receber, via renderização, qualquer indicação de
    /// <see cref="RetryEnabled"/> — a UI para não autorizados deve ser idêntica com o gate ligado ou desligado.
    /// </summary>
    public bool IsRetryOperator { get; private set; }

    /// <summary>
    /// Verdadeiro só quando o gate está habilitado, o usuário é Operator/Administrator, E o estado atual do
    /// domínio permite retry (somente RetryScheduled). Isto é UX — o backend revalida tudo no POST.
    /// </summary>
    public bool CanRequestRetry { get; private set; }

    /// <summary>Modo de demonstração ativo: a página exibe dados sintéticos e nenhuma escrita é possível.</summary>
    public bool PresentationMode { get; private set; }

    /// <summary>Chave de idempotência gerada no servidor para o formulário desta renderização.</summary>
    public Guid IdempotencyKey { get; private set; }

    /// <summary>Renderiza a leitura + (quando permitido) o formulário de retry.</summary>
    public async Task<IActionResult> OnGetAsync(Guid? jobId, CancellationToken cancellationToken)
    {
        JobIdValue = jobId ?? Guid.Empty;

        // Modo de demonstração: dataset sintético em memória, ZERO acesso a SQL, retry sempre desabilitado.
        if (_presentation.Enabled)
        {
            PresentationMode = true;
            RetryEnabled = true;
            IsRetryOperator = false;
            CanRequestRetry = false;
            Job = JobIdValue == Guid.Empty ? null : _presentationData.GetJobs().FirstOrDefault(j => j.JobId == JobIdValue);
            Transitions = JobIdValue == Guid.Empty ? [] : _presentationData.GetJobTransitions(JobIdValue);
            return Page();
        }

        var scope = _scopeAccessor.Resolve(User);

        RetryEnabled = _retryGate.Enabled;
        // Calculada ANTES e INDEPENDENTEMENTE do gate: um usuário sem este mandato nunca deve conseguir
        // distinguir, pela resposta do GET, se o gate está ligado ou desligado (ver IsRetryOperator).
        IsRetryOperator = (await _authorization.AuthorizeAsync(User, JobRetryOperatorsPolicy).ConfigureAwait(false)).Succeeded;

        Job = JobIdValue == Guid.Empty
            ? null
            : (await _queries.ListJobsAsync(scope, 200, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(j => j.JobId == JobIdValue);
        Transitions = JobIdValue == Guid.Empty
            ? []
            : await _queries.ListJobTransitionsAsync(scope, JobIdValue, cancellationToken).ConfigureAwait(false);

        // O domínio só considera retry manual elegível a partir de RetryScheduled — nunca a partir de um
        // estado terminal (Completed/Failed/Cancelled) nem de Pending/Processing. Puramente informativo:
        // o backend do POST revalida a elegibilidade independentemente do que foi calculado aqui.
        CanRequestRetry = RetryEnabled && IsRetryOperator && Job is { State: nameof(JobState.RetryScheduled) };

        // Uma chave de idempotência POR renderização do formulário: reenvio/double-click carrega a MESMA
        // chave (embutida em <input hidden>) ⇒ 1 efeito lógico.
        IdempotencyKey = Guid.NewGuid();

        return Page();
    }

    /// <summary>
    /// SOLICITA retry manual autorizado de um job. Aceita do navegador SOMENTE <paramref name="jobId"/> e
    /// <paramref name="idempotencyKey"/>; tenant/projeto/usuário/correlação são resolvidos server-side. O
    /// <paramref name="jobId"/> é entrada NÃO CONFIÁVEL — é re-resolvido dentro do escopo autorizado pela
    /// própria consulta/store (cross-tenant/cross-project é indistinguível de inexistente). Fail-closed em
    /// todo caminho de erro, com auditoria da tentativa.
    /// Rate limiting (Passo 8): política "sensitive-write" (partição por usuário autenticado), aplicada por
    /// <see cref="ArchiveBridge.ControlPlane.Composition.SensitiveOperationRateLimitingMiddleware"/> ANTES
    /// deste handler ser alcançado.
    /// </summary>
    public async Task<IActionResult> OnPostRetryAsync(Guid jobId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        // STOP-THE-LINE: no modo de demonstração NENHUMA operação de negócio é executada — nem SQL, nem
        // transição, nem auditoria. Recusa antes de tocar qualquer store; a demonstração é somente leitura.
        if (_presentation.Enabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var correlation = CorrelationId.New();
        var username = User.Identity?.Name ?? string.Empty;

        // Identidade do principal (fail-closed): sem UserId UTILIZÁVEL não há como responsabilizar — 403.
        if (!Guid.TryParse(User.FindFirstValue(PortalClaims.UserId), out var userId) || userId == Guid.Empty)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Escopo derivado EXCLUSIVAMENTE do principal autenticado; claims inválidas ⇒ fail-closed.
        TenantScope scope;
        try
        {
            scope = _scopeAccessor.Resolve(User);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Autorização RBAC SERVER-SIDE: Operator/Administrator apenas. PRECEDE o feature gate — um principal
        // sem mandato NUNCA infere o estado do gate pela resposta (403 idêntico com Enabled=true/false).
        var authorized = await _authorization.AuthorizeAsync(User, JobRetryOperatorsPolicy).ConfigureAwait(false);
        if (!authorized.Succeeded)
        {
            await AuditAsync(scope, userId, username, jobId, false, "forbidden", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Somente APÓS a autorização: feature gate do deployment.
        if (!_retryGate.Enabled)
        {
            await AuditAsync(scope, userId, username, jobId, false, "feature-disabled", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Validação de entrada básica: identificadores GUID não vazios. Inválido ⇒ 400, zero efeito.
        if (jobId == Guid.Empty || idempotencyKey == Guid.Empty)
        {
            await AuditAsync(scope, userId, username, jobId, false, "invalid-input", correlation, cancellationToken)
                .ConfigureAwait(false);
            return BadRequest();
        }

        var outcome = await _requestRetry.ExecuteAsync(
            new RequestJobRetry(scope, new JobId(jobId), idempotencyKey, correlation), cancellationToken)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case JobRetryRequestOutcome.NotFound:
                // Inexistente OU cross-tenant/cross-project são EXTERNAMENTE indistinguíveis (anti-IDOR).
                await AuditAsync(scope, userId, username, jobId, false, "not-found-or-not-authorized", correlation, cancellationToken)
                    .ConfigureAwait(false);
                return NotFound();

            case JobRetryRequestOutcome.NotEligible:
                await AuditAsync(scope, userId, username, jobId, false, "precondition-failed", correlation, cancellationToken)
                    .ConfigureAwait(false);
                return StatusCode(StatusCodes.Status409Conflict);

            case JobRetryRequestOutcome.IdempotencyConflict:
                await AuditAsync(scope, userId, username, jobId, false, "idempotency-conflict", correlation, cancellationToken)
                    .ConfigureAwait(false);
                return StatusCode(StatusCodes.Status409Conflict);

            case JobRetryRequestOutcome.Applied:
            case JobRetryRequestOutcome.IdempotentReplay:
                // Auditoria de RESULTADO antes do redirect. Se persistir a trilha falhar, a exceção
                // propaga (HTTP 500): a solicitação já foi aplicada de forma durável e idempotente; um
                // retry do POST com a MESMA chave devolve o MESMO resultado (idempotência).
                var reason = outcome == JobRetryRequestOutcome.IdempotentReplay ? "idempotent-replay" : "accepted";
                await AuditAsync(scope, userId, username, jobId, true, reason, correlation, cancellationToken)
                    .ConfigureAwait(false);

                // PRG: sucesso/replay redirecionam para o GET canônico (nunca renderiza após POST).
                return RedirectToPage("/Jobs/Details", new { jobId });

            default:
                // FAIL-CLOSED: qualquer valor de outcome não reconhecido explicitamente (evolução futura do
                // enum, mapeamento inesperado da infraestrutura, corrupção) NUNCA é promovido implicitamente
                // a sucesso. Audita a anomalia como falha (nunca "accepted"/"idempotent-replay") e então
                // lança — não há StatusCode(500) manual porque o pipeline (sem middleware de exceção em
                // Development; UseExceptionHandler("/Error") fora de Development) já converte exceção não
                // tratada em 500 sanitizado, sem vazar detalhes internos ao chamador e sem redirecionar como
                // se a operação tivesse sido aceita.
                await AuditAsync(scope, userId, username, jobId, false, "unexpected-outcome", correlation, cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"{nameof(JobRetryRequestOutcome)} não reconhecido pelo handler de retry manual do Passo 7.");
        }
    }

    private Task AuditAsync(
        TenantScope scope,
        Guid userId,
        string username,
        Guid jobId,
        bool succeeded,
        string reason,
        CorrelationId correlation,
        CancellationToken cancellationToken) =>
        _operationalAudit.RecordAsync(
            scope,
            new PortalOperationalAuditEvent(
                userId, username, ActionCode, ResourceType, jobId.ToString("N"), succeeded, reason, correlation.Value, _clock.UtcNow),
            cancellationToken);
}
