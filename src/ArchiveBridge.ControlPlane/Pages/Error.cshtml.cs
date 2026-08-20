using ArchiveBridge.ControlPlane.Composition;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ArchiveBridge.ControlPlane.Pages;

/// <summary>
/// Página de erro amigável e consistente. Renderiza um estado visual por classe de código HTTP sem revelar
/// detalhes internos nem informação cross-tenant/projeto. É acionada pelo <c>UseExceptionHandler</c> (500) e
/// também acessível diretamente com <c>?code=</c> para os demais estados (400/403/404/409/503).
/// </summary>
public sealed class ErrorModel : PageModel
{
    /// <summary>Código HTTP representado.</summary>
    public int Code { get; private set; } = StatusCodes.Status500InternalServerError;

    /// <summary>Título curto do estado.</summary>
    public string Heading { get; private set; } = "Erro inesperado";

    /// <summary>Mensagem genérica (nunca detalhe interno).</summary>
    public string Message { get; private set; } = "Ocorreu um erro ao processar sua solicitação.";

    /// <summary>Variante visual do cartão de aviso.</summary>
    public string Variant { get; private set; } = "danger";

    /// <summary>
    /// Correlation ID da requisição que originou o erro (Passo 8), atribuído por
    /// <see cref="RequestCorrelationMiddleware"/>. Não é um detalhe interno — é a referência que o operador
    /// pode informar ao suporte para localizar a linha correspondente nos logs estruturados do servidor.
    /// </summary>
    public string? CorrelationId { get; private set; }

    /// <summary>Resolve o estado a partir do código informado (query) ou do status corrente da resposta.</summary>
    public void OnGet()
    {
        CorrelationId = HttpContext.GetCorrelationId();
        var code = HttpContext.Response.StatusCode;
        var raw = Request.Query["code"].ToString();
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 400 and < 600)
        {
            code = parsed;
        }

        Code = code;
        (Heading, Message, Variant) = code switch
        {
            400 => ("Requisição inválida", "A solicitação não pôde ser processada. Verifique os dados informados e tente novamente.", "warn"),
            403 => ("Acesso não autorizado", "Seu perfil não possui permissão para executar esta operação.", "warn"),
            404 => ("Recurso não encontrado", "O recurso solicitado não existe ou não está disponível neste escopo.", "info"),
            409 => ("Conflito", "A operação não pôde ser concluída por um conflito de estado. Recarregue e tente novamente.", "warn"),
            503 => ("Funcionalidade indisponível", "Este recurso não está habilitado neste deployment.", "info"),
            _ => ("Erro inesperado", "Ocorreu um erro ao processar sua solicitação. A equipe de operação foi registrada nos logs do servidor.", "danger"),
        };
    }
}
