namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>
/// Limites de tamanho de página das consultas paginadas do portal. O <c>pageSize</c> vindo do navegador NUNCA
/// controla o <c>TOP</c> sem limite: é sempre normalizado (clamp) server-side para <c>[MinPageSize,
/// MaxPageSize]</c> de forma determinística.
/// </summary>
public static class KeysetPaging
{
    /// <summary>Tamanho de página padrão quando o cliente não informa um valor válido.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Menor tamanho de página aceito.</summary>
    public const int MinPageSize = 1;

    /// <summary>Maior tamanho de página aceito (teto de exaustão de recursos).</summary>
    public const int MaxPageSize = 100;

    /// <summary>Normaliza (clamp determinístico) um tamanho de página solicitado para o intervalo permitido.</summary>
    public static int ClampPageSize(int requested) => Math.Clamp(requested, MinPageSize, MaxPageSize);
}

/// <summary>
/// Página keyset/seek: os itens desta fatia e, quando há mais, a POSIÇÃO tipada da última linha entregue —
/// usada pela camada de apresentação para emitir o próximo cursor. Não carrega contagem total (sem
/// <c>COUNT(*)</c> a cada navegação) nem número de página.
/// </summary>
public sealed record KeysetPage<TItem, TPosition>(
    IReadOnlyList<TItem> Items,
    TPosition? NextPosition,
    bool HasMore)
    where TPosition : class, IKeysetPosition;

/// <summary>
/// Posição de seek (as chaves de ordenação de uma linha). É o único conteúdo "de navegação" que um cursor
/// carrega — NUNCA tenant/projeto, papel, usuário, SQL, caminho físico ou segredo. Um cursor válido de uma
/// consulta jamais troca o escopo: o servidor sempre re-resolve tenant/projeto a partir do principal.
/// </summary>
public interface IKeysetPosition
{
    /// <summary>Verdadeiro somente quando todas as chaves de ordenação estão presentes e são plausíveis.</summary>
    bool IsWellFormed();
}

/// <summary>
/// Posição de seek do histórico de evidências: <c>completed_at_utc</c> DESC, <c>environment_id</c> DESC,
/// <c>discovery_version</c> DESC.
/// </summary>
public sealed record EvidenceSeekPosition(DateTimeOffset CompletedAtUtc, Guid EnvironmentId, int DiscoveryVersion)
    : IKeysetPosition
{
    /// <inheritdoc />
    public bool IsWellFormed() => CompletedAtUtc != default && EnvironmentId != Guid.Empty && DiscoveryVersion >= 1;
}

/// <summary>
/// Posição de seek das trilhas de auditoria (operacional e de autenticação): <c>occurred_at_utc</c> DESC,
/// <c>event_id</c> DESC.
/// </summary>
public sealed record AuditSeekPosition(DateTimeOffset OccurredAtUtc, long EventId) : IKeysetPosition
{
    /// <inheritdoc />
    public bool IsWellFormed() => OccurredAtUtc != default && EventId >= 1;
}
