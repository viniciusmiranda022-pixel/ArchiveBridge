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
/// Lançada quando o texto CSV é estruturalmente malformado (aspas no meio de campo não citado,
/// texto após o fechamento de um campo citado, aspas não fechadas, etc.). O parser falha fechado —
/// não tenta reparar a estrutura.
/// </summary>
public sealed class MappingCsvFormatException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MappingCsvFormatException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MappingCsvFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MappingCsvFormatException(string message, Exception innerException)
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
