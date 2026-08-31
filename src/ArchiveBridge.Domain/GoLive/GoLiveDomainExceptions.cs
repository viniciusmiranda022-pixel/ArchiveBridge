namespace ArchiveBridge.Domain.GoLive;

/// <summary>
/// Lançada quando uma <see cref="GoLiveAuthorizationDecision"/> JÁ PERSISTIDA falha a revalidação de
/// integridade — a persistência é fronteira NÃO CONFIÁVEL: um registro adulterado/corrompido nunca é
/// reidratado, devolvido, verificado como válido ou autorreparado silenciosamente (AB-I8-010 escopo
/// obrigatório item 10).
/// </summary>
public sealed class GoLiveIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public GoLiveIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public GoLiveIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public GoLiveIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta avaliar/autorizar go-live sem que NENHUM plano de canário exista ainda para o
/// escopo (tenant, project) — não há dependência alguma para vincular/julgar (AB-I8-010, escopo obrigatório
/// item 2). Nenhuma decisão é persistida quando esta exceção é lançada.
/// </summary>
public sealed class GoLiveEntryGateBlockedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public GoLiveEntryGateBlockedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public GoLiveEntryGateBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public GoLiveEntryGateBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
