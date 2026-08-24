namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Lançada por <see cref="PurviewUploadRequest.Rehydrate"/> quando uma linha JÁ PERSISTIDA viola o
/// invariante de identidade determinística do agregado (<c>request_hash</c> recomputado diverge do
/// persistido). Fail-closed: uma linha corrompida ou adulterada nunca é reidratada, devolvida ou
/// reaproveitada como se fosse um pedido válido.
/// </summary>
public sealed class PurviewUploadIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewUploadIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewUploadIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewUploadIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela store quando uma tentativa concorrente já persistiu o pedido CANÔNICO para a mesma
/// (tenant, projeto, wave) — o índice único no SQL Server venceu a corrida antes desta chamada. Fail-closed:
/// nenhuma linha duplicada é criada; o chamador deve reler o pedido canônico existente (item 8: "nunca
/// duplicar um upload lógico silenciosamente"), nunca tratar isto como falha de negócio.
/// </summary>
public sealed class PurviewUploadRequestConflictException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewUploadRequestConflictException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewUploadRequestConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewUploadRequestConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando um <see cref="PurviewUploadRequestConflictException"/> foi capturado mas
/// a releitura subsequente NÃO encontrou o pedido canônico — estado impossível sob o invariante de
/// canonicidade (quem venceu a corrida DEVERIA estar persistido).
/// </summary>
public sealed class PurviewUploadRequestConflictUnresolvedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PurviewUploadRequestConflictUnresolvedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PurviewUploadRequestConflictUnresolvedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PurviewUploadRequestConflictUnresolvedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
