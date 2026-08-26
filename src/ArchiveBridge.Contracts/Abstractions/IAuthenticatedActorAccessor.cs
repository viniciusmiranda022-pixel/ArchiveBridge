namespace ArchiveBridge.Contracts.Abstractions;

/// <summary>
/// Identidade canônica e papéis efetivos do chamador autenticado, resolvidos INTEIRAMENTE server-side a
/// partir do mecanismo de sessão/principal do host — nunca de um valor fornecido pelo próprio payload da
/// requisição (AB-I6-012).
/// </summary>
/// <param name="ActorId">Identidade estável do ator (nunca vazia/whitespace nesta representação).</param>
/// <param name="Roles">Papéis efetivos do ator nesta sessão (pode ser vazio — nenhum papel concedido).</param>
public sealed record AuthenticatedActor(string ActorId, IReadOnlyCollection<string> Roles);

/// <summary>
/// Ator autenticado do chamador atual (porta). Casos de uso que precisam da identidade/papéis efetivos do
/// principal para decidir RBAC e para auditoria (quem decidiu) dependem desta abstração em vez de aceitar
/// ator/papel como campos de um comando fornecido pelo chamador — um payload controlado pelo cliente nunca
/// pode ser autoridade de autorização ou de auditoria (AB-I6-012, corrigindo o bypass de RBAC do
/// <c>DisposeReconciliationExceptionCommand</c> original do AB-I6-010).
///
/// Fail-closed: a implementação de produção lança quando não há principal autenticado válido no contexto
/// atual — nunca retorna um ator "anônimo" ou sintético silenciosamente.
/// </summary>
public interface IAuthenticatedActorAccessor
{
    /// <summary>
    /// Ator autenticado do chamador atual. Lança quando não há principal autenticado válido no contexto
    /// atual (fail-closed) — a leitura desta propriedade deve ocorrer ANTES de qualquer acesso a dado de
    /// escopo, para que a ausência de autenticação nunca revele existência de dados.
    /// </summary>
    AuthenticatedActor Current { get; }
}
