namespace ArchiveBridge.Domain.Security;

/// <summary>Lançada quando um <see cref="WdacPolicyEvidence"/> JÁ PERSISTIDO falha a revalidação de integridade (entradas/digest/hash adulterados).</summary>
public sealed class WdacPolicyIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WdacPolicyIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WdacPolicyIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WdacPolicyIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Lançada quando uma entrada/policy WDAC violaria um invariante estrutural — em especial, qualquer combinação que equivaleria a allow-all.</summary>
public sealed class WdacPolicyInvariantViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public WdacPolicyInvariantViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public WdacPolicyInvariantViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public WdacPolicyInvariantViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
