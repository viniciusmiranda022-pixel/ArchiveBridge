namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Disponibilidade DESCOBERTA de uma capacidade do ambiente Enterprise Vault. Uma capacidade só é
/// <see cref="Available"/> com evidência POSITIVA — nunca por inferência da versão textual. A ausência
/// de evidência é <see cref="Indeterminate"/> (ou <see cref="Unavailable"/>/<see cref="PermissionDenied"/>/
/// <see cref="Unsupported"/> conforme a política), jamais <see cref="Available"/> por omissão.
/// </summary>
public enum CapabilityAvailability
{
    /// <summary>Comprovada por evidência positiva no ambiente autorizado.</summary>
    Available,

    /// <summary>Comprovadamente ausente (evidência negativa: módulo/cmdlet/parâmetro não existe).</summary>
    Unavailable,

    /// <summary>Sem evidência suficiente para afirmar presença ou ausência (fail-closed).</summary>
    Indeterminate,

    /// <summary>A descoberta foi barrada por permissão insuficiente — presença indeterminada.</summary>
    PermissionDenied,

    /// <summary>O ambiente/versão não é suportado por nenhum adapter conhecido para esta capacidade.</summary>
    Unsupported,
}

/// <summary>
/// Estado versionado de uma avaliação de descoberta de capacidades. Máquina de estados fail-closed: a
/// descoberta começa <see cref="Pending"/>, passa por <see cref="Discovering"/> e termina em exatamente
/// um estado terminal (<see cref="Ready"/>/<see cref="Blocked"/>/<see cref="Unsupported"/>/
/// <see cref="Failed"/>). Uma descoberta posterior, COMPLETA e validada, marca a anterior como
/// <see cref="Superseded"/> — a evidência antiga é preservada, nunca sobrescrita.
/// </summary>
public enum EvDiscoveryStatus
{
    /// <summary>Reservada/enfileirada; nenhuma sonda executada ainda.</summary>
    Pending,

    /// <summary>Sondagem read-only em andamento.</summary>
    Discovering,

    /// <summary>Ambiente pronto: as capacidades exigidas foram comprovadas.</summary>
    Ready,

    /// <summary>Ambiente bloqueado: falta capacidade/permissão comprovada (recuperável após correção).</summary>
    Blocked,

    /// <summary>Ambiente não suportado por nenhum adapter conhecido (não recuperável sem novo adapter).</summary>
    Unsupported,

    /// <summary>A descoberta falhou (erro de sonda/timeout/saída inválida); um retry cria nova execução.</summary>
    Failed,

    /// <summary>Substituída por uma descoberta posterior completa e validada (evidência preservada).</summary>
    Superseded,
}

/// <summary>Transição de estado de descoberta inválida (fail-closed).</summary>
public sealed class InvalidEvDiscoveryTransitionException(EvDiscoveryStatus from, EvDiscoveryStatus to)
    : Exception($"Transição de descoberta inválida: {from} → {to}.")
{
    /// <summary>Estado de origem recusado.</summary>
    public EvDiscoveryStatus From { get; } = from;

    /// <summary>Estado de destino recusado.</summary>
    public EvDiscoveryStatus To { get; } = to;
}

/// <summary>
/// Transições permitidas da descoberta (fail-closed): tudo que não estiver explicitamente listado é
/// recusado. Os estados terminais de uma execução só evoluem para <see cref="EvDiscoveryStatus.Superseded"/>
/// quando uma execução posterior é finalizada; um <see cref="EvDiscoveryStatus.Failed"/> não volta a
/// executar (o retry cria uma nova execução).
/// </summary>
public static class EvDiscoveryTransitions
{
    private static readonly Dictionary<EvDiscoveryStatus, IReadOnlySet<EvDiscoveryStatus>> Allowed =
        new()
        {
            [EvDiscoveryStatus.Pending] = new HashSet<EvDiscoveryStatus> { EvDiscoveryStatus.Discovering, EvDiscoveryStatus.Failed },
            [EvDiscoveryStatus.Discovering] = new HashSet<EvDiscoveryStatus>
            {
                EvDiscoveryStatus.Ready, EvDiscoveryStatus.Blocked, EvDiscoveryStatus.Unsupported, EvDiscoveryStatus.Failed,
            },
            [EvDiscoveryStatus.Ready] = new HashSet<EvDiscoveryStatus> { EvDiscoveryStatus.Superseded },
            [EvDiscoveryStatus.Blocked] = new HashSet<EvDiscoveryStatus> { EvDiscoveryStatus.Superseded },
            [EvDiscoveryStatus.Unsupported] = new HashSet<EvDiscoveryStatus> { EvDiscoveryStatus.Superseded },
            [EvDiscoveryStatus.Failed] = new HashSet<EvDiscoveryStatus> { EvDiscoveryStatus.Superseded },
            [EvDiscoveryStatus.Superseded] = new HashSet<EvDiscoveryStatus>(),
        };

    /// <summary>Indica se a transição <paramref name="from"/> → <paramref name="to"/> é permitida.</summary>
    public static bool CanTransition(EvDiscoveryStatus from, EvDiscoveryStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Garante a transição; lança <see cref="InvalidEvDiscoveryTransitionException"/> se recusada.</summary>
    public static void EnsureCanTransition(EvDiscoveryStatus from, EvDiscoveryStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidEvDiscoveryTransitionException(from, to);
        }
    }

    /// <summary>Estados terminais de uma execução (não sondam mais; só podem ser substituídos).</summary>
    public static bool IsTerminal(EvDiscoveryStatus status) =>
        status is EvDiscoveryStatus.Ready or EvDiscoveryStatus.Blocked
            or EvDiscoveryStatus.Unsupported or EvDiscoveryStatus.Failed or EvDiscoveryStatus.Superseded;
}
