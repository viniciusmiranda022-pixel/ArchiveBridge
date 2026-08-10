using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Resultado de um enfileiramento IDEMPOTENTE de descoberta. <see cref="Created"/> indica que o Job e o
/// comando foram criados agora; <see cref="Replayed"/> indica que uma requisição anterior com a MESMA chave
/// de idempotência (mesmo tenant/projeto/comando) já havia criado o Job e este é apenas um replay — os dois
/// casos devolvem o MESMO <see cref="JobId"/>. Exatamente um dos dois é verdadeiro.
/// </summary>
public sealed record EvDiscoveryEnqueueResult(JobId JobId, bool Created, bool Replayed);

/// <summary>
/// Lançada quando uma chave de idempotência já existente está vinculada a um comando de descoberta
/// DIFERENTE (ambiente/contexto divergente). A chave nunca é reutilizada silenciosamente para outro
/// comando — o chamador deve tratar como conflito determinístico (ex.: HTTP 409) e auditar.
/// </summary>
public sealed class EvDiscoveryIdempotencyConflictException(string message) : Exception(message);
