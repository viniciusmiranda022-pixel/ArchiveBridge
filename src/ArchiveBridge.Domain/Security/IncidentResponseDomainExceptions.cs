namespace ArchiveBridge.Domain.Security;

/// <summary>Lançada quando um <see cref="IncidentResponseDrillRecord"/> JÁ PERSISTIDO falha a revalidação de integridade.</summary>
public sealed class IncidentResponseIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public IncidentResponseIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public IncidentResponseIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public IncidentResponseIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Lançada quando um <see cref="IncidentResponseDrillRecord"/> violaria um invariante estrutural (ex.: disposition com aparência de segredo/PII, timestamps invertidos).</summary>
public sealed class IncidentResponseInvariantViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public IncidentResponseInvariantViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public IncidentResponseInvariantViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public IncidentResponseInvariantViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
