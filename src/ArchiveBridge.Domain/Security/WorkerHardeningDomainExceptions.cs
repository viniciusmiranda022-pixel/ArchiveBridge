namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Lançada quando um <see cref="WorkerHardeningControlRecord"/> JÁ PERSISTIDO falha a revalidação de
/// integridade — a persistência é fronteira NÃO CONFIÁVEL, mesmo princípio de
/// <see cref="ArchiveBridge.Domain.Recovery.RecoveryReadinessIntegrityViolationException"/>.
/// </summary>
public sealed class WorkerHardeningIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WorkerHardeningIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WorkerHardeningIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WorkerHardeningIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando se tenta construir um <see cref="WorkerHardeningControlRecord"/> que violaria um
/// invariante estrutural — <see cref="WorkerHardeningStatus.Pass"/> sem medição real, Pass para um
/// controle <see cref="WorkerHardeningApplicability.Unsupported"/>, ou Blocked sem medição e sem motivo
/// documentado.
/// </summary>
public sealed class WorkerHardeningInvariantViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WorkerHardeningInvariantViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WorkerHardeningInvariantViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WorkerHardeningInvariantViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
