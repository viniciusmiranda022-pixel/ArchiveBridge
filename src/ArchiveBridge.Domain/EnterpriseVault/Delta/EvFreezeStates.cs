namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Estado de planejamento/autorização de freeze/cutover de UM archive (AB-4C-008 req 9-11; runbook §16.5
/// passos 31-35). NUNCA representa execução real — apenas estado e autorização formal. Nenhuma transição
/// desta máquina aciona qualquer ação destrutiva ou operacional real no Enterprise Vault; a ação real
/// permanece, neste Passo, inteiramente fora de escopo (STOP-THE-LINE).
/// </summary>
public enum EvFreezeStatus
{
    /// <summary>Nenhum freeze solicitado ainda (estado inicial, após baseline).</summary>
    NotRequested,

    /// <summary>Freeze foi solicitado: necessário antes de qualquer delta final (passo 31).</summary>
    FreezeRequired,

    /// <summary>Freeze foi FORMALMENTE autorizado por operador/role competente, com justificativa e correlação persistidas.</summary>
    FreezeAuthorized,

    /// <summary>Autorização de freeze foi recusada (precondição não satisfeita ou role inválido) — pode ser re-solicitado.</summary>
    FreezeRejected,

    /// <summary>O delta final foi concluído com sucesso sob freeze autorizado — pronto para cutover (passo 32).</summary>
    FinalDeltaReady,

    /// <summary>Cutover concluído; o EV deve ser preservado pelo período de rollback contratual (passo 34).</summary>
    RollbackRetentionRequired,

    /// <summary>
    /// Descomissionamento permanece BLOQUEADO até sign-off/retenção/reconciliação posteriores (passo 35) —
    /// terminal e nunca liberado por esta máquina de estados neste Passo.
    /// </summary>
    DecommissionBlocked,
}

/// <summary>Transição de estado de freeze inválida (fail-closed).</summary>
public sealed class InvalidEvFreezeTransitionException(EvFreezeStatus from, EvFreezeStatus to)
    : Exception($"Transição de freeze inválida: {from} → {to}.")
{
    /// <summary>Estado de origem recusado.</summary>
    public EvFreezeStatus From { get; } = from;

    /// <summary>Estado de destino recusado.</summary>
    public EvFreezeStatus To { get; } = to;
}

/// <summary>
/// Transições permitidas do plano de freeze (fail-closed): tudo que não estiver explicitamente listado é
/// recusado. <see cref="EvFreezeStatus.DecommissionBlocked"/> não tem transições de saída — o
/// desbloqueio de descomissionamento é gate de um Passo posterior, nunca desta máquina de estados.
/// </summary>
public static class EvFreezeTransitions
{
    private static readonly Dictionary<EvFreezeStatus, IReadOnlySet<EvFreezeStatus>> Allowed =
        new()
        {
            [EvFreezeStatus.NotRequested] = new HashSet<EvFreezeStatus> { EvFreezeStatus.FreezeRequired },
            [EvFreezeStatus.FreezeRequired] = new HashSet<EvFreezeStatus>
            {
                EvFreezeStatus.FreezeAuthorized, EvFreezeStatus.FreezeRejected,
            },
            [EvFreezeStatus.FreezeRejected] = new HashSet<EvFreezeStatus> { EvFreezeStatus.FreezeRequired },
            [EvFreezeStatus.FreezeAuthorized] = new HashSet<EvFreezeStatus> { EvFreezeStatus.FinalDeltaReady },
            [EvFreezeStatus.FinalDeltaReady] = new HashSet<EvFreezeStatus> { EvFreezeStatus.RollbackRetentionRequired },
            [EvFreezeStatus.RollbackRetentionRequired] = new HashSet<EvFreezeStatus> { EvFreezeStatus.DecommissionBlocked },
            [EvFreezeStatus.DecommissionBlocked] = new HashSet<EvFreezeStatus>(),
        };

    /// <summary>Indica se a transição <paramref name="from"/> → <paramref name="to"/> é permitida.</summary>
    public static bool CanTransition(EvFreezeStatus from, EvFreezeStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Garante a transição; lança <see cref="InvalidEvFreezeTransitionException"/> se recusada.</summary>
    public static void EnsureCanTransition(EvFreezeStatus from, EvFreezeStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidEvFreezeTransitionException(from, to);
        }
    }
}
