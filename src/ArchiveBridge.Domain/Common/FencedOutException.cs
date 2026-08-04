namespace ArchiveBridge.Domain.Common;

/// <summary>
/// Lançada quando os efeitos de uma operação durável seriam gravados por um trabalhador que perdeu o
/// cercamento (fencing) do Job — o lease expirou, foi recuperado pelo reaper e/ou reivindicado por
/// outra época/worker. Fail-closed: a gravação é recusada na MESMA transação dos efeitos, de modo que
/// nenhum efeito parcial é persistido por um dono defasado. Distinta de <see cref="ConcurrencyException"/>
/// (row_version otimista): esta é o cercamento do Job (owner_worker + lease_epoch + lease válido).
/// </summary>
public sealed class FencedOutException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public FencedOutException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public FencedOutException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public FencedOutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
