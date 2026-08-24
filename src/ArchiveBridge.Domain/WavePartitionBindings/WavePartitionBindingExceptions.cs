namespace ArchiveBridge.Domain.WavePartitionBindings;

/// <summary>
/// Lançada por <see cref="WavePartitionOutputBinding.Rehydrate"/> quando uma linha JÁ PERSISTIDA viola o
/// invariante de identidade determinística do agregado (<c>binding_hash</c> recomputado diverge do
/// persistido) — a persistência é uma fronteira NÃO CONFIÁVEL: uma linha corrompida ou adulterada nunca é
/// reidratada, devolvida ou reaproveitada como se fosse um vínculo válido. Fail-closed.
/// </summary>
public sealed class WavePartitionOutputBindingIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WavePartitionOutputBindingIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WavePartitionOutputBindingIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WavePartitionOutputBindingIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela store quando uma tentativa concorrente já persistiu o vínculo CANÔNICO para a mesma
/// (tenant, projeto, wave, plano, parte) — o índice único no SQL Server venceu a corrida antes desta
/// chamada. Fail-closed: nenhuma linha duplicada é criada; o chamador deve reler o vínculo canônico
/// existente (convergência idempotente), nunca tratar isto como falha de negócio.
/// </summary>
public sealed class WavePartitionOutputBindingConflictException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WavePartitionOutputBindingConflictException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WavePartitionOutputBindingConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WavePartitionOutputBindingConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application (item 4) quando o vínculo CANÔNICO já existente para (wave, plano, parte) NÃO
/// é o mesmo output lógico do candidato (<see cref="WavePartitionOutputBinding.IsSameLogicalOutputAs"/>
/// falso) — uma tentativa de remapear a mesma identidade lógica para um output incompatível. Fail-closed:
/// NUNCA substitui silenciosamente a evidência anterior (o vínculo é append-oriented/imutável); a
/// divergência exige decisão explícita fora deste caminho de código.
/// </summary>
public sealed class WavePartitionOutputBindingIncompatibleException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WavePartitionOutputBindingIncompatibleException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WavePartitionOutputBindingIncompatibleException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WavePartitionOutputBindingIncompatibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando a onda ou a execução de partição canônica referenciadas por um pedido de
/// vínculo não existem OU não pertencem ao escopo tenant/projeto do chamador. Deliberadamente
/// indistinguível entre as causas (anti-IDOR — nunca revela existência de uma onda/execução de outro
/// tenant/projeto, nem se a onda existe mas a execução não, ou vice-versa).
/// </summary>
public sealed class WavePartitionOutputBindingSourceNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WavePartitionOutputBindingSourceNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WavePartitionOutputBindingSourceNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WavePartitionOutputBindingSourceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada pela Application quando um <see cref="WavePartitionOutputBindingConflictException"/> foi
/// capturado mas a releitura subsequente NÃO encontrou o vínculo canônico — estado impossível sob o
/// invariante de canonicidade (quem venceu a corrida DEVERIA estar persistido).
/// </summary>
public sealed class WavePartitionOutputBindingConflictUnresolvedException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WavePartitionOutputBindingConflictUnresolvedException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WavePartitionOutputBindingConflictUnresolvedException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WavePartitionOutputBindingConflictUnresolvedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
