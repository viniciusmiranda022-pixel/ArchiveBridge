namespace ArchiveBridge.Contracts.Abstractions;

/// <summary>
/// Relógio abstrato (porta). Permite tempo determinístico nos testes e uma única fonte de
/// "agora" em UTC na aplicação. A implementação de produção lê o relógio do sistema.
/// </summary>
public interface IClock
{
    /// <summary>Instante atual em UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
