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

/// <summary>
/// Lançada quando um comando durável está OBSOLETO em relação ao estado corrente: a versão/estado que
/// o originou (schema do comando, versão/hash de configuração do projeto, versão/hash de seleção da
/// onda, pasta de destino ou versões de esquema/gerador/política do mapping) não coincide mais com o
/// agregado carregado — por exemplo, a seleção avançou para uma versão posterior. Fail-closed: o
/// comando NUNCA é executado silenciosamente contra uma versão diferente; falha terminalmente com o
/// código <see cref="Code"/>.
/// </summary>
public sealed class StaleCommandContextException : Exception
{
    /// <summary>Código estável do erro (para diagnóstico/telemetria), sem PII.</summary>
    public const string Code = "STALE_COMMAND_CONTEXT";

    /// <summary>Cria a exceção sem mensagem.</summary>
    public StaleCommandContextException()
        : base(Code)
    {
    }

    /// <summary>Cria a exceção com detalhe (prefixado pelo código).</summary>
    public StaleCommandContextException(string message)
        : base($"{Code}: {message}")
    {
    }

    /// <summary>Cria a exceção com detalhe e causa.</summary>
    public StaleCommandContextException(string message, Exception innerException)
        : base($"{Code}: {message}", innerException)
    {
    }
}
