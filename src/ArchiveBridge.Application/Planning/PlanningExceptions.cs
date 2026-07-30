namespace ArchiveBridge.Application.Planning;

/// <summary>Lançada quando o projeto/onda alvo não existe no escopo (inclui barragem cross-tenant pela RLS).</summary>
public sealed class PlanningNotFoundException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PlanningNotFoundException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PlanningNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PlanningNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando uma validação de planejamento falha de forma fechada (ex.: hash de configuração
/// inconsistente, ou estado que não permite (re)validação). Nunca contém PII.
/// </summary>
public sealed class PlanningValidationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public PlanningValidationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public PlanningValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public PlanningValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
