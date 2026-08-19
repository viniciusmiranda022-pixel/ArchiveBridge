namespace ArchiveBridge.Contracts.Mapping;

/// <summary>
/// Lançada quando uma chave de idempotência de validação já está vinculada a uma requisição
/// semanticamente DIFERENTE (onda/versão/hashes/esquema/política/code page/SHA divergentes). Nenhuma
/// nova tentativa é criada — a chave não pode ser reaproveitada para outro conteúdo/contexto.
/// </summary>
public sealed class MappingValidationConflictException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MappingValidationConflictException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MappingValidationConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MappingValidationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lançada quando a onda-fonte autorizada mudou entre a leitura/validação e a persistência (defesa
/// TOCTOU): a versão/hashes divergem do snapshot validado ou o estado deixou de ser Approved/Frozen no
/// momento do commit. Nenhuma tentativa é persistida (fail-closed) — a custódia jamais afirma ter
/// validado um snapshot que não é mais o corrente.
/// </summary>
public sealed class MappingValidationStaleSourceException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MappingValidationStaleSourceException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MappingValidationStaleSourceException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MappingValidationStaleSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
