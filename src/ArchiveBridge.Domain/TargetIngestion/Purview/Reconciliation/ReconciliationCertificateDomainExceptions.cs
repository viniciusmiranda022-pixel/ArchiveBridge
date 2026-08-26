namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Lançada quando um certificate JÁ PERSISTIDO falha a revalidação de integridade (o
/// <see cref="ReconciliationCertificate.CertificateHash"/> recomputado diverge do persistido) — a
/// persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de <see cref="ReconciliationIntegrityViolationException"/>):
/// um certificate adulterado/corrompido nunca é reidratado, devolvido, verificado como válido ou
/// autorreparado silenciosamente (AB-I6-013 itens 12/14/83).
/// </summary>
public sealed class ReconciliationCertificateIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationCertificateIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationCertificateIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationCertificateIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando o certificate referenciado (onda/plano/versão) não existe ou não pertence ao escopo
/// tenant/projeto do chamador — mesmo padrão anti-IDOR de <see cref="ReconciliationExceptionNotFoundException"/>
/// (nunca revela existência de um certificate de outro tenant/projeto/onda, nem distingue "inexistente" de
/// "fora de escopo").
/// </summary>
public sealed class ReconciliationCertificateNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationCertificateNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationCertificateNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationCertificateNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando o ator autenticado não tem papel autorizado a emitir um certificate (AB-I6-013 item 19) —
/// a mensagem é sempre genérica e NUNCA distingue "papel insuficiente" de "onda/plano inexistente",
/// preservando o mesmo comportamento anti-enumeração de <see cref="ReconciliationCertificateNotFoundException"/>.
/// </summary>
public sealed class ReconciliationCertificateAuthorizationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationCertificateAuthorizationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationCertificateAuthorizationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationCertificateAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando a evidência canônica (avaliação de reconciliação e/ou dispositions vigentes) mudou
/// concorrentemente ENTRE a resolução do candidato pela Application e a persistência sob lock — a emissão é
/// recusada fail-closed em vez de produzir um certificate baseado em um snapshot misto de evidência antiga e
/// nova (AB-I6-013 item 17/49). O chamador deve reler o estado atual e tentar novamente.
/// </summary>
public sealed class ReconciliationCertificateStaleChainException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ReconciliationCertificateStaleChainException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ReconciliationCertificateStaleChainException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ReconciliationCertificateStaleChainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
