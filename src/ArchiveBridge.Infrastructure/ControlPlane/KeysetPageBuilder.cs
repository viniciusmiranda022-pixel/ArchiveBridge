using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Monta uma <see cref="KeysetPage{TItem,TPosition}"/> a partir das linhas efetivamente lidas quando a
/// consulta pediu <c>TOP (pageSize + 1)</c>. Se veio a linha extra, há mais páginas: ela é removida e a
/// próxima posição de seek é construída a partir da ÚLTIMA linha realmente ENTREGUE (nunca da linha extra).
/// </summary>
internal static class KeysetPageBuilder
{
    public static KeysetPage<TItem, TPosition> Build<TItem, TPosition>(
        List<TItem> fetched, int pageSize, Func<TItem, TPosition> toPosition)
        where TPosition : class, IKeysetPosition
    {
        var hasMore = fetched.Count > pageSize;
        if (hasMore)
        {
            fetched.RemoveAt(fetched.Count - 1); // descarta a linha-sentinela extra
        }

        var next = hasMore && fetched.Count > 0 ? toPosition(fetched[^1]) : null;
        return new KeysetPage<TItem, TPosition>(fetched, next, hasMore);
    }
}
