namespace ArchiveBridge.Domain.Common;

/// <summary>
/// Lançada quando um UPDATE otimista não afeta nenhuma linha porque o <see cref="RowVersion"/>
/// esperado divergiu (a linha foi alterada por outra transação desde a leitura). Fail-closed: a
/// operação NÃO é reportada como sucesso — o chamador deve reler o estado atual e decidir de novo.
/// </summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ConcurrencyException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
