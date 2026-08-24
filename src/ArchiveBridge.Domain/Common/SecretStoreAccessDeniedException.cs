namespace ArchiveBridge.Domain.Common;

/// <summary>
/// Uma tentativa de acesso a um segredo custodiado foi recusada fail-closed pelo adapter de secret store
/// (identidade de workload não autorizada, ou referência inexistente/fora do escopo — as duas causas
/// produzem o MESMO tipo/mensagem genérica, indistinguível de inexistente). Defesa em profundidade: o
/// adapter concreto (ex.: <c>DpapiSecretStore</c>) revalida a identidade independentemente da Application,
/// que já recusa antes de chamar o adapter.
/// </summary>
public sealed class SecretStoreAccessDeniedException : Exception
{
    /// <summary>Cria a exceção com mensagem.</summary>
    public SecretStoreAccessDeniedException(string message)
        : base(message)
    {
    }
}
