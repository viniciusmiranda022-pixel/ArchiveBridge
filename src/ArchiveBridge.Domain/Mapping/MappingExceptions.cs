namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Lançada quando o mapping não pode ser gerado com segurança a partir da fonte autorizada
/// (ex.: limite de linhas excedido, onda não aprovada, code page fora da política). Fail-closed.
/// </summary>
public sealed class MappingGenerationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MappingGenerationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MappingGenerationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MappingGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando um valor autorizado começaria por um caractere interpretável como fórmula
/// (<c>= + - @</c>, tabulação ou CR). A geração falha em vez de alterar silenciosamente o valor —
/// a mitigação nunca reescreve o dado autorizado.
/// </summary>
public sealed class MappingCsvInjectionException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MappingCsvInjectionException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MappingCsvInjectionException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MappingCsvInjectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
