using System.Security.Claims;
using ArchiveBridge.Application.Mapping;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages.Mapping;

/// <summary>
/// Mapping — leitura do mapping gerado e, para Operator/Administrator com o gate habilitado, a superfície
/// HTTP/Portal que RECEBE e VALIDA um <c>mapping.csv</c> reutilizando integralmente o backend seguro do
/// Passo 6A (<see cref="ValidateMappingCsvUploadUseCase"/> + <see cref="IMappingValidationStore"/>). Este
/// handler não gera mapping, não exporta nada do Enterprise Vault e não importa nada para o Microsoft 365
/// — apenas recebe, valida e custodia a evidência de recepção de um CSV contra uma onda já aprovada/congelada.
/// </summary>
[RequestSizeLimit(IndexModel.HttpMultipartBoundBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = IndexModel.HttpMultipartBoundBytes)]
public sealed class IndexModel(
    IControlPlaneQueries queries,
    IPortalScopeAccessor scopeAccessor,
    IAuthorizationService authorization,
    IMappingValidationStore validationStore,
    ValidateMappingCsvUploadUseCase validateUpload,
    IPortalOperationalAudit operationalAudit,
    MappingUploadLimits uploadLimits,
    MappingUploadPortalOptions uploadGate,
    Composition.PresentationModeOptions presentation,
    Presentation.IPresentationDataProvider presentationData,
    IClock clock) : PageModel
{
    /// <summary>Código de ação da trilha operacional para a submissão de validação de Mapping CSV.</summary>
    public const string ActionCode = "mapping.validation.submit";
    private const string ResourceType = "mapping-validation";
    private const string MappingValidationOperatorsPolicy = "MappingValidationOperators";

    // Teto HTTP/multipart: o ABSOLUTO estrutural (50 MiB) + margem fixa para boundary/campos do formulário.
    // Nunca depende de configuração — o limite EFETIVO (que pode ser bem menor) é aplicado pelo backend 6A
    // (autoridade final), este valor apenas evita que a camada HTTP aceite um corpo ilimitado. Público para
    // ser referenciável pelo atributo <see cref="RequestSizeLimitAttribute"/> aplicado à classe.
    public const long HttpMultipartBoundBytes = MappingUploadLimits.AbsoluteMaxUploadBytes + (64 * 1024);

    private readonly IControlPlaneQueries _queries = queries;
    private readonly IPortalScopeAccessor _scopeAccessor = scopeAccessor;
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IMappingValidationStore _validationStore = validationStore;
    private readonly ValidateMappingCsvUploadUseCase _validateUpload = validateUpload;
    private readonly IPortalOperationalAudit _operationalAudit = operationalAudit;
    private readonly MappingUploadLimits _uploadLimits = uploadLimits;
    private readonly MappingUploadPortalOptions _uploadGate = uploadGate;
    private readonly Composition.PresentationModeOptions _presentation = presentation;
    private readonly Presentation.IPresentationDataProvider _presentationData = presentationData;
    private readonly IClock _clock = clock;

    /// <summary>Mapping atualmente gerado do projeto (somente dataset sintético, hoje). Mesmo contrato pré-existente.</summary>
    public Presentation.MappingOverview? Mapping { get; private set; }

    /// <summary>Ondas elegíveis (Approved/Frozen) do projeto para o seletor de upload.</summary>
    public IReadOnlyList<WaveOption> EligibleWaves { get; private set; } = [];

    /// <summary>Gate do deployment: o Portal pode aceitar upload/validação de Mapping CSV?</summary>
    public bool UploadEnabled { get; private set; }

    /// <summary>
    /// Autorização RBAC (Operator/Administrator) calculada INDEPENDENTEMENTE do estado do gate. Um principal
    /// sem este mandato NUNCA deve receber, via renderização, qualquer indicação de <see cref="UploadEnabled"/>
    /// — a UI para não autorizados deve ser idêntica com o gate ligado ou desligado.
    /// </summary>
    public bool IsUploadOperator { get; private set; }

    /// <summary>Verdadeiro só quando o gate está habilitado E o usuário é Operator/Administrator.</summary>
    public bool CanUploadMapping { get; private set; }

    /// <summary>Modo de demonstração ativo: a página exibe dados sintéticos e nenhuma escrita é possível.</summary>
    public bool PresentationMode { get; private set; }

    /// <summary>Limite efetivo real (configurado) de bytes de upload — nunca hardcoded na página.</summary>
    public long EffectiveMaxUploadBytes { get; private set; }

    /// <summary>Chave de idempotência gerada no servidor para o formulário desta renderização.</summary>
    public Guid IdempotencyKey { get; private set; }

    /// <summary>Resultado canônico carregado quando a query string traz um <c>validationId</c> conhecido.</summary>
    public MappingValidationAttempt? ValidationResult { get; private set; }

    /// <summary>Renderiza a leitura + (quando permitido) o formulário de upload/validação.</summary>
    public async Task<IActionResult> OnGetAsync(Guid? validationId, CancellationToken cancellationToken)
    {
        // Modo de demonstração: dataset sintético em memória, ZERO acesso a SQL, controles de upload desabilitados.
        if (_presentation.Enabled)
        {
            PresentationMode = true;
            UploadEnabled = true;
            CanUploadMapping = false;
            EffectiveMaxUploadBytes = _uploadLimits.EffectiveMaxUploadBytes;
            Mapping = _presentationData.GetMapping();
            return Page();
        }

        var scope = _scopeAccessor.Resolve(User);

        UploadEnabled = _uploadGate.Enabled;
        // Calculada ANTES e INDEPENDENTEMENTE do gate: um usuário sem este mandato nunca deve conseguir
        // distinguir, pela resposta do GET, se o gate está ligado ou desligado (ver IsUploadOperator).
        IsUploadOperator = (await _authorization.AuthorizeAsync(User, MappingValidationOperatorsPolicy).ConfigureAwait(false)).Succeeded;
        CanUploadMapping = UploadEnabled && IsUploadOperator;
        EffectiveMaxUploadBytes = _uploadLimits.EffectiveMaxUploadBytes;

        // Apenas ondas em estado imutável (Approved/Frozen) são fonte autorizada de validação. A filtragem
        // aqui é VISUAL/conveniência — a autorização real de cada onda é revalidada server-side no POST.
        var projectWaves = await _queries.ListWavesAsync(scope, max: 200, cancellationToken).ConfigureAwait(false);
        EligibleWaves = projectWaves
            .Where(w => w.Status is nameof(WaveStatus.Approved) or nameof(WaveStatus.Frozen))
            .Select(w => new WaveOption(w.WaveId, w.Name, w.Status))
            .ToList();

        // Uma chave de idempotência POR renderização do formulário: reenvio/double-click carrega a MESMA
        // chave (embutida em <input hidden>) ⇒ 1 tentativa custodiada.
        IdempotencyKey = Guid.NewGuid();

        if (validationId is { } id)
        {
            var attempt = await _validationStore.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            if (attempt is null)
            {
                // Inexistente OU de outro tenant/projeto são EXTERNAMENTE indistinguíveis (anti-IDOR).
                return NotFound();
            }

            ValidationResult = attempt;
        }

        return Page();
    }

    /// <summary>
    /// RECEBE e VALIDA um upload de Mapping CSV via <see cref="ValidateMappingCsvUploadUseCase"/>. Aceita do
    /// navegador SOMENTE <paramref name="waveId"/>, <paramref name="contentCodePage"/>,
    /// <paramref name="idempotencyKey"/> e o arquivo; tenant/projeto/usuário/correlação são resolvidos
    /// server-side. Fail-closed em todo caminho de erro, com auditoria da tentativa e SEM persistir bytes
    /// brutos do CSV em storage da aplicação (o buffer transitório do model binding de <see cref="IFormFile"/>
    /// é do framework, nunca escrito em evidence root/landing/staging).
    /// </summary>
    public async Task<IActionResult> OnPostValidateCsvAsync(
        Guid waveId, int contentCodePage, Guid idempotencyKey, IFormFile? file, CancellationToken cancellationToken)
    {
        // STOP-THE-LINE: no modo de demonstração NENHUMA operação de negócio é executada — nem SQL, nem
        // validação, nem auditoria. Recusa antes de tocar qualquer store; a demonstração é somente leitura.
        if (_presentation.Enabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var correlation = CorrelationId.New();
        var username = User.Identity?.Name ?? string.Empty;

        // Identidade do principal (fail-closed): sem UserId UTILIZÁVEL não há como responsabilizar — 403.
        // Ausente, malformada ou Guid.Empty são tratadas igualmente: recusa ANTES de qualquer store/auditoria
        // de negócio (o backend 6A também rejeita Guid.Empty, mas com uma exceção de argumento — a decisão de
        // autorização é desta camada e deve ser observável como 403, nunca como erro do servidor).
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
        var authorized = await _authorization.AuthorizeAsync(User, MappingValidationOperatorsPolicy).ConfigureAwait(false);
        if (!authorized.Succeeded)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "forbidden", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Somente APÓS a autorização: feature gate do deployment.
        if (!_uploadGate.Enabled)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "feature-disabled", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Validação de entrada básica: onda/chave não vazias e arquivo presente e não vazio.
        if (waveId == Guid.Empty || idempotencyKey == Guid.Empty || file is null || file.Length == 0)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "invalid-input", correlation, cancellationToken)
                .ConfigureAwait(false);
            return BadRequest();
        }

        ContentCodePage codePage;
        try
        {
            codePage = new ContentCodePage(contentCodePage);
        }
        catch (ArgumentOutOfRangeException)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "invalid-input", correlation, cancellationToken)
                .ConfigureAwait(false);
            return BadRequest();
        }

        MappingUploadValidationOutcome outcome;
        try
        {
            await using var content = file.OpenReadStream();
            outcome = await _validateUpload.ExecuteAsync(
                new MappingCsvUploadRequest(
                    scope,
                    new WaveId(waveId),
                    codePage,
                    content,
                    file.FileName,
                    file.Length,
                    userId,
                    username,
                    idempotencyKey,
                    correlation),
                MappingPolicy.Default,
                cancellationToken).ConfigureAwait(false);
        }
        catch (MappingUploadRejectedException ex) when (
            ex.Reason is MappingUploadRejectionReason.ContentTooLarge or MappingUploadRejectionReason.DeclaredLengthTooLarge)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "payload-too-large", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (MappingUploadRejectedException)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "invalid-input", correlation, cancellationToken)
                .ConfigureAwait(false);
            return BadRequest();
        }
        catch (PlanningNotFoundException)
        {
            // Onda inexistente/cross-project/cross-tenant são EXTERNAMENTE indistinguíveis (anti-IDOR).
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "not-found-or-not-authorized", correlation, cancellationToken)
                .ConfigureAwait(false);
            return NotFound();
        }
        catch (MappingUploadPreconditionException)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "precondition-failed", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status409Conflict);
        }
        catch (MappingValidationConflictException)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "idempotency-conflict", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status409Conflict);
        }
        catch (MappingValidationStaleSourceException)
        {
            await AuditAsync(scope, userId, username, waveId.ToString("N"), false, "precondition-failed", correlation, cancellationToken)
                .ConfigureAwait(false);
            return StatusCode(StatusCodes.Status409Conflict);
        }

        // Auditoria de RESULTADO antes do redirect (usa o ValidationId canônico como resourceId — nunca
        // filename/mailbox/path/PST/cell values). Se a auditoria falhar, a exceção propaga (HTTP 500): a
        // tentativa durável já foi custodiada e o retry com a MESMA chave devolve o MESMO ValidationId.
        var reason = outcome.Replayed ? "idempotent-replay" : "accepted";
        await AuditAsync(scope, userId, username, outcome.ValidationId.ToString("N"), true, reason, correlation, cancellationToken)
            .ConfigureAwait(false);

        // PRG: sucesso/replay redirecionam para o GET canônico do resultado (nunca renderiza após POST).
        return RedirectToPage("/Mapping/Index", new { validationId = outcome.ValidationId });
    }

    private Task AuditAsync(
        TenantScope scope,
        Guid userId,
        string username,
        string resourceId,
        bool succeeded,
        string reason,
        CorrelationId correlation,
        CancellationToken cancellationToken) =>
        _operationalAudit.RecordAsync(
            scope,
            new PortalOperationalAuditEvent(
                userId, username, ActionCode, ResourceType, resourceId, succeeded, reason, correlation.Value, _clock.UtcNow),
            cancellationToken);

    /// <summary>Onda elegível (Approved/Frozen) oferecida no seletor de upload.</summary>
    public sealed record WaveOption(Guid WaveId, string Name, string Status);
}
