namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>Pedido/archive/tentativa de exportação inexistente ou fora do escopo (anti-IDOR, indistinguível de inexistente).</summary>
public sealed class EvExportNotFoundException(string message) : Exception(message);

/// <summary>
/// Bloqueio fail-closed ANTES de qualquer execução: capability de exportação ausente, schema desconhecido,
/// snap-in indisponível ou connector revogado (AB-4C-005 item 8). Nunca executa quando lançada.
/// </summary>
public sealed class EvExportCapabilityBlockedException(string blockingReason) : Exception(
    $"Exportação bloqueada: capability EV não certificada ({blockingReason}).")
{
    /// <summary>Diagnóstico estruturado do bloqueio (mesmo vocabulário do handshake do Passo 1).</summary>
    public string BlockingReason { get; } = blockingReason;
}

/// <summary>Concorrência não autorizada detectada por connector ou archive — pedido enfileirado/recusado (item 4).</summary>
public sealed class EvExportThrottledException(string message) : Exception(message);

/// <summary>
/// O diretório de saída resolvido escapou da raiz local autorizada do connector — traversal, UNC não
/// allowlisted, symlink/junction/reparse point ou qualquer outra forma de escape de containment (§15.2,
/// item 9). Fail-closed: nenhum processo é iniciado.
/// </summary>
public sealed class EvExportOutputContainmentException(string message) : Exception(message);

/// <summary>
/// Revalidação de outputs físicos falhou: arquivo ausente, hash divergente, ou o conjunto físico diverge do
/// manifesto persistido (item 13). Fail-closed — nunca aceita um replay como sucesso sob esta condição.
/// </summary>
public sealed class EvExportIntegrityViolationException(string message) : Exception(message);

/// <summary>Uma chave de idempotência já está vinculada a um pedido de exportação de conteúdo DIVERGENTE.</summary>
public sealed class EvExportIdempotencyConflictException(string message) : Exception(message);

/// <summary>Argumento/estado de exportação inválido (fail-closed na borda de validação).</summary>
public sealed class EvExportValidationException(string message) : Exception(message);
