using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Contracts.EnterpriseVault.Connector;

/// <summary>Resultado de um append: o snapshot vigente e se uma linha NOVA foi criada agora.</summary>
public sealed record InventorySnapshotAppendResult(InventorySnapshot Snapshot, bool Created);

/// <summary>
/// Store append-only de snapshots de inventário. A decisão de "isto é um réplay idêntico" (mesmo hash do
/// último snapshot) é tomada pela Application ANTES de chamar <see cref="AppendAsync"/> — este store
/// apenas insere a versão decidida de forma segura sob concorrência (dois envios concorrentes do MESMO
/// connector calculando a mesma próxima versão convergem para uma única linha, nunca duas).
/// </summary>
public interface IConnectorInventoryStore
{
    /// <summary>Devolve o snapshot de maior versão dentro do escopo; <see langword="null"/> se nenhum existir.</summary>
    Task<InventorySnapshot?> GetLatestAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken);

    /// <summary>
    /// Insere o snapshot informado. Sob corrida (duas chamadas concorrentes com a mesma
    /// <see cref="InventorySnapshot.Version"/> para o mesmo connector), a segunda converge para a linha já
    /// persistida pela primeira (<c>Created = false</c>) em vez de falhar ou duplicar.
    /// </summary>
    Task<InventorySnapshotAppendResult> AppendAsync(InventorySnapshot snapshot, CancellationToken cancellationToken);
}
