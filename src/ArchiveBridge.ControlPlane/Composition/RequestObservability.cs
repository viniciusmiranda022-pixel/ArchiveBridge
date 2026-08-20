using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;

namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Rate limiting (Passo 8) das quatro operações sensíveis JÁ EXISTENTES do Slice 4A — nenhum novo write-path
/// é criado. Implementado como middleware dedicado (em vez do atributo <c>[EnableRateLimiting]</c>) porque o
/// Razor Pages resolve o HANDLER (<c>OnPostXxxAsync</c>) DEPOIS do roteamento de endpoint: o atributo aplicado
/// a um handler específico não é observável pelo middleware de rate limiting nativo, que decide a partir dos
/// metadados do ENDPOINT (uma página inteira, não um handler). Este middleware, portanto, reconhece as quatro
/// rotas/handlers explicitamente por caminho + verbo + <c>?handler=</c> — a mesma granularidade que o atributo
/// pretendia expressar — e nunca depende de metadado de endpoint.
/// </summary>
public sealed class SensitiveOperationRateLimiter : IDisposable
{
    // Partições em memória (deployment on-premises de instância única, consistente com a arquitetura
    // aprovada) — sem persistência, sem nova migration. QueueLimit 0: estourar o limite responde 429
    // imediatamente (mensagem genérica, fail-closed, sem enfileirar a requisição).
    private readonly PartitionedRateLimiter<string> _login = PartitionedRateLimiter.Create<string, string>(
        key => RateLimitPartition.GetFixedWindowLimiter(
            key, _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    private readonly PartitionedRateLimiter<string> _sensitiveWrite = PartitionedRateLimiter.Create<string, string>(
        key => RateLimitPartition.GetFixedWindowLimiter(
            key, _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    /// <summary>Tenta consumir uma permissão da política "login" (partição por endereço remoto).</summary>
    public RateLimitLease AttemptAcquireLogin(string partitionKey) => _login.AttemptAcquire(partitionKey);

    /// <summary>Tenta consumir uma permissão da política "sensitive-write" (partição por usuário autenticado).</summary>
    public RateLimitLease AttemptAcquireSensitiveWrite(string partitionKey) => _sensitiveWrite.AttemptAcquire(partitionKey);

    public void Dispose()
    {
        _login.Dispose();
        _sensitiveWrite.Dispose();
    }
}

/// <summary>
/// Reconhece as quatro requisições POST sensíveis do Slice 4A por caminho + <c>?handler=</c> e aplica a
/// política correspondente de <see cref="SensitiveOperationRateLimiter"/> ANTES de alcançar autorização/
/// antiforgery/o handler. Qualquer outra requisição passa direto, sem custo. Resposta de estouro: <c>429</c>
/// genérico, sem detalhe interno (mesma política de erro das demais respostas fail-closed desta fatia).
/// </summary>
public sealed class SensitiveOperationRateLimitingMiddleware(RequestDelegate next, SensitiveOperationRateLimiter limiter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var policy = ResolvePolicy(context);
        if (policy is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        RateLimitLease lease;
        if (policy == RateLimitPolicy.Login)
        {
            lease = limiter.AttemptAcquireLogin(remoteAddress);
        }
        else
        {
            var partitionKey = context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst(PortalClaims.UserId)?.Value ?? "authenticated-without-claim"
                : remoteAddress;
            lease = limiter.AttemptAcquireSensitiveWrite(partitionKey);
        }

        using (lease)
        {
            if (!lease.IsAcquired)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            await next(context).ConfigureAwait(false);
        }
    }

    private static string? ResolvePolicy(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return null;
        }

        var path = context.Request.Path;
        if (path.Equals("/Account/Login", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPolicy.Login;
        }

        var handler = context.Request.Query["handler"].ToString();
        var isSensitiveWrite =
            (path.Equals("/EnterpriseVault/Index", StringComparison.OrdinalIgnoreCase) && handler == "RequestDiscovery") ||
            (path.Equals("/Mapping/Index", StringComparison.OrdinalIgnoreCase) && handler == "ValidateCsv") ||
            (path.Equals("/Jobs/Details", StringComparison.OrdinalIgnoreCase) && handler == "Retry");

        return isSensitiveWrite ? RateLimitPolicy.SensitiveWrite : null;
    }

    private static class RateLimitPolicy
    {
        public const string Login = "login";
        public const string SensitiveWrite = "sensitive-write";
    }
}

/// <summary>
/// Métricas operacionais do host do Portal (Passo 8): latência, falhas e contagem de operações por
/// requisição HTTP. Publicadas via <see cref="System.Diagnostics.Metrics.Meter"/> nativo do .NET — sem
/// exportador externo obrigatório (coletável por <c>dotnet-counters</c>/OpenTelemetry on-premises quando
/// decidido). Nenhuma tag carrega segredo, PII, caminho físico ou valor de negócio; apenas método, template
/// de rota e classe de status HTTP.
/// </summary>
public sealed class ControlPlaneMetrics : IDisposable
{
    /// <summary>Nome do meter, usado por quem for coletar as métricas (ex.: dotnet-counters).</summary>
    public const string MeterName = "ArchiveBridge.ControlPlane";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _duration;

    public ControlPlaneMetrics()
    {
        _meter = new Meter(MeterName);
        _requests = _meter.CreateCounter<long>(
            "archivebridge_controlplane_http_requests_total", unit: "{request}",
            description: "Total de requisições HTTP concluídas, por método e status.");
        _failures = _meter.CreateCounter<long>(
            "archivebridge_controlplane_http_failures_total", unit: "{request}",
            description: "Total de requisições HTTP concluídas com status >= 400.");
        _duration = _meter.CreateHistogram<double>(
            "archivebridge_controlplane_http_request_duration_ms", unit: "ms",
            description: "Duração da requisição HTTP, do início do pipeline até a resposta concluída.");
    }

    /// <summary>Registra uma requisição concluída (sempre chamado; sucesso ou falha).</summary>
    public void RecordRequest(string method, int statusCode, double elapsedMilliseconds)
    {
        var statusClass = statusCode switch
        {
            >= 500 => "5xx",
            >= 400 => "4xx",
            >= 300 => "3xx",
            >= 200 => "2xx",
            _ => "other",
        };

        var tags = new TagList { { "method", method }, { "status_class", statusClass } };
        _requests.Add(1, tags);
        _duration.Record(elapsedMilliseconds, tags);
        if (statusCode >= 400)
        {
            _failures.Add(1, tags);
        }
    }

    public void Dispose() => _meter.Dispose();
}

/// <summary>Chave usada em <see cref="HttpContext.Items"/> para o correlation ID da requisição corrente.</summary>
public static class RequestCorrelation
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemsKey = "ArchiveBridge.CorrelationId";

    /// <summary>Correlation ID da requisição corrente, se já atribuído por <see cref="RequestCorrelationMiddleware"/>.</summary>
    public static string? GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(ItemsKey, out var value) ? value as string : null;

    internal static void SetCorrelationId(this HttpContext context, string correlationId) =>
        context.Items[ItemsKey] = correlationId;
}

/// <summary>
/// Middleware de observabilidade por requisição (Passo 8): atribui/propaga um <c>correlation ID</c> por
/// requisição (aceita um valor do cliente em <c>X-Correlation-Id</c> quando plausível — apenas para permitir
/// correlação ponta-a-ponta com um proxy/gateway on-premises; NUNCA é usado para autorização/escopo), o
/// devolve no cabeçalho de resposta, registra uma linha de log estruturado por requisição (método, rota,
/// status, duração, correlation ID) e alimenta <see cref="ControlPlaneMetrics"/>. Nunca registra corpo,
/// query string, cookie, senha ou segredo — apenas metadados estruturados da requisição.
/// </summary>
public sealed partial class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger, ControlPlaneMetrics metrics)
{
    // Aceita o correlation ID do cliente apenas se parecer um identificador plausível (evita poluir logs/
    // métricas com valores arbitrariamente grandes ou com caracteres de controle vindos de um cliente hostil).
    private const int MaxIncomingCorrelationIdLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveIncomingCorrelationId(context) ?? Guid.NewGuid().ToString("D");
        context.SetCorrelationId(correlationId);
        context.Response.Headers[RequestCorrelation.HeaderName] = correlationId;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            {
                await next(context).ConfigureAwait(false);
            }
        }
        finally
        {
            stopwatch.Stop();
            var method = context.Request.Method;
            var statusCode = context.Response.StatusCode;
            var elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            metrics.RecordRequest(method, statusCode, elapsedMilliseconds);

            var logLevel = statusCode >= 500 ? LogLevel.Error : statusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
            LogRequestCompleted(logLevel, method, context.Request.Path.Value, statusCode, elapsedMilliseconds, correlationId);
        }
    }

    [LoggerMessage(Message = "HTTP {Method} {Path} respondeu {StatusCode} em {ElapsedMilliseconds} ms [correlation={CorrelationId}]")]
    private partial void LogRequestCompleted(
        LogLevel level, string method, string? path, int statusCode, double elapsedMilliseconds, string correlationId);

    private static string? ResolveIncomingCorrelationId(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(RequestCorrelation.HeaderName, out var values))
        {
            return null;
        }

        var candidate = values.ToString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxIncomingCorrelationIdLength)
        {
            return null;
        }

        foreach (var character in candidate)
        {
            // Alfabeto conservador (evita CR/LF/controle vazando para logs em texto e cabeçalhos malformados).
            var isAllowed = char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':';
            if (!isAllowed)
            {
                return null;
            }
        }

        return candidate;
    }
}
