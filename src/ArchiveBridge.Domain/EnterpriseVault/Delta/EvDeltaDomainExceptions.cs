namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>Execução/watermark/plano de freeze inexistente ou fora do escopo — anti-IDOR, indistinguível de inexistente.</summary>
public sealed class EvDeltaNotFoundException(string message) : Exception(message);

/// <summary>
/// Bloqueio fail-closed ANTES de qualquer chamada ao adapter EV: nenhuma delta strategy elegível para a
/// versão/fase pedida (AB-4C-008 req 2). Nunca chama o adapter quando lançada.
/// </summary>
public sealed class EvDeltaStrategyUnsupportedException(string reason) : Exception(
    $"Nenhuma delta strategy elegível ({reason}) — fail-closed.")
{
    /// <summary>Diagnóstico estruturado (mesmo vocabulário do desfecho de seleção — nunca mensagem livre).</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Watermark candidato recusado: stale, cross-scope, de outra strategy ou downgrade de versão
/// (AB-4C-008 req 13). Nunca aceito como canônico quando lançada.
/// </summary>
public sealed class EvWatermarkRejectedException(EvWatermarkRejectionReason reason, string message) : Exception(message)
{
    /// <summary>Motivo estruturado da rejeição.</summary>
    public EvWatermarkRejectionReason Reason { get; } = reason;
}

/// <summary>Uma chave de idempotência de execução já está vinculada a uma execução de conteúdo DIVERGENTE.</summary>
public sealed class EvDeltaIdempotencyConflictException(string message) : Exception(message);

/// <summary>Argumento/estado de delta inválido (fail-closed na borda de validação).</summary>
public sealed class EvDeltaValidationException(string message) : Exception(message);

/// <summary>Autorização de freeze ausente, com role inválido, ou exigida como precondição não satisfeita (fail-closed).</summary>
public sealed class EvFreezeAuthorizationRequiredException(string message) : Exception(message);

/// <summary>
/// FinalDelta solicitado sem freeze formalmente autorizado para o archive (STOP-THE-LINE): nenhum delta
/// final é elegível fora da janela autorizada (runbook §16.5 passo 31/32).
/// </summary>
public sealed class EvFreezeNotAuthorizedException(string message) : Exception(message);
