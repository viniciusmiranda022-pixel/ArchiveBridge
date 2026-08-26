namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Lançada quando a exceção referenciada (onda/plano/versão de avaliação/item) não existe ou não pertence
/// ao escopo tenant/projeto do chamador — mesmo padrão anti-IDOR de
/// <c>PurviewImportJobSourceNotFoundException</c> (nunca revela existência de uma exceção de outro
/// tenant/projeto/onda, nem distingue "item inexistente" de "fora de escopo").
/// </summary>
public sealed class ReconciliationExceptionNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationExceptionNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationExceptionNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationExceptionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando o ator autenticado não tem papel autorizado a criar/alterar disposition (item 5 do work
/// order) — a mensagem é sempre genérica e NUNCA distingue "papel insuficiente" de "exceção inexistente",
/// preservando o mesmo comportamento anti-enumeração de <see cref="ReconciliationExceptionNotFoundException"/>.
/// </summary>
public sealed class ReconciliationExceptionAuthorizationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationExceptionAuthorizationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationExceptionAuthorizationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationExceptionAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando o item referenciado não pode receber disposition: é
/// <see cref="ReconciliationDisposition.MatchedWithinEvidence"/> (item 11 — não é uma exceção) ou
/// <see cref="ReconciliationDisposition.BlockedIntegrity"/> (item 13 — indeclinável/inaudível como sucesso;
/// somente nova evidência/reconciliação válida remove o bloqueio, nunca uma decisão humana).
/// </summary>
public sealed class ReconciliationExceptionNotDispositionableException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationExceptionNotDispositionableException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationExceptionNotDispositionableException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationExceptionNotDispositionableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando a versão de avaliação referenciada pela decisão não é mais a vigente (foi superseded por
/// uma nova avaliação desde que o chamador a observou) — item 8 do work order: a disposition sobre a
/// avaliação antiga é sempre recusada fail-closed; nunca decide sobre uma evidência técnica potencialmente
/// obsoleta.
/// </summary>
public sealed class ReconciliationExceptionStaleAssessmentException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationExceptionStaleAssessmentException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationExceptionStaleAssessmentException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationExceptionStaleAssessmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando a decisão solicitada (status/motivo/comentário) viola um invariante estrutural do
/// workflow — combinação status/motivo não permitida para o <see cref="ReconciliationDisposition"/> técnico
/// do item, motivo fora do catálogo fechado, ou comentário fora dos limites de tamanho/caracteres
/// permitidos (item 16). Fail-closed: nenhuma decisão parcial ou "melhor esforço" é jamais persistida.
/// </summary>
public sealed class ReconciliationExceptionDispositionValidationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationExceptionDispositionValidationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationExceptionDispositionValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationExceptionDispositionValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
