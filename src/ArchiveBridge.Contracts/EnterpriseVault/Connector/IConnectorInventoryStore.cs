using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Contracts.EnterpriseVault.Connector;

/// <summary>Resultado de um append: o snapshot vigente e se uma linha NOVA foi criada agora.</summary>
public sealed record InventorySnapshotAppendResult(InventorySnapshot Snapshot, bool Created);

/// <summary>
/// Store append-only de snapshots de inventário. A decisão de "isto é um réplay idêntico" (mesmo hash do
/// último snapshot CONHECIDO no momento da leitura) é tomada pela Application ANTES de chamar
/// <see cref="AppendAsync"/> — mas sob corrida, o snapshot que acabou de se tornar o latest pode ter um
/// conteúdo diferente do que a Application leu; por isso <see cref="AppendAsync"/> também verifica
/// semanticamente antes de decidir convergir (ver abaixo).
/// </summary>
public interface IConnectorInventoryStore
{
    /// <summary>
    /// Devolve o snapshot de maior versão dentro do escopo; <see langword="null"/> se nenhum existir. A
    /// persistência é fronteira NÃO CONFIÁVEL (AB-4C-003): uma linha cuja evidência hashada não corresponde
    /// aos archives filhos realmente carregados falha fechado em vez de ser devolvida como canônica.
    /// </summary>
    /// <exception cref="ArchiveBridge.Domain.EnterpriseVault.Connector.InventorySnapshotIntegrityViolationException">
    /// O snapshot persistido está corrompido ou adulterado (hash/archive_count divergente, ou IDs de archive
    /// duplicados entre os filhos carregados).
    /// </exception>
    Task<InventorySnapshot?> GetLatestAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken);

    /// <summary>
    /// Insere o snapshot informado. Sob corrida (duas chamadas concorrentes com a mesma
    /// <see cref="InventorySnapshot.Version"/> para o mesmo connector), o resultado depende do CONTEÚDO da
    /// linha já persistida nessa versão, nunca só da versão colidida: se o <see cref="InventorySnapshot.SnapshotHash"/>
    /// já persistido é IGUAL ao do <paramref name="snapshot"/> informado, converge para a linha já gravada
    /// (<c>Created = false</c>, réplay idêntico, seguro). Se o hash DIVERGE, a versão foi legitimamente
    /// ocupada por outra mudança real — nunca é tratada como réplay; o método lança
    /// <see cref="ArchiveBridge.Domain.Common.ConcurrencyException"/> para o chamador reler o latest e
    /// tentar novamente com a próxima versão disponível, sem perder a mudança em silêncio.
    /// </summary>
    /// <exception cref="ArchiveBridge.Domain.Common.ConcurrencyException">
    /// A versão colidiu com um snapshot já persistido de conteúdo diferente (retriable).
    /// </exception>
    Task<InventorySnapshotAppendResult> AppendAsync(InventorySnapshot snapshot, CancellationToken cancellationToken);
}
