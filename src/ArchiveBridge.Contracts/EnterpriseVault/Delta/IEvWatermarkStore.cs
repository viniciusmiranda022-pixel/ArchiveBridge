using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Contracts.EnterpriseVault.Delta;

/// <summary>
/// Store append-only de watermarks (AB-4C-008 req 5): cada watermark aceito é uma linha imutável nova — a
/// evidência de um watermark anterior nunca é reescrita nem removida. Um novo watermark só se torna
/// CANÔNICO após o resultado necessário da fase que o produziu estar persistido/validado (req 6/14) — a
/// Application garante essa ordem, nunca este store.
/// </summary>
public interface IEvWatermarkStore
{
    /// <summary>Persiste um novo watermark (append) — nunca sobrescreve um watermark já persistido.</summary>
    Task AppendAsync(TenantScope scope, EvWatermark watermark, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve o watermark CANÔNICO vigente do archive (mais recente por <see cref="EvWatermark.IssuedAtUtc"/>)
    /// dentro do escopo; <see langword="null"/> se nenhum existir ainda (o próximo pedido deve ser Baseline).
    /// </summary>
    Task<EvWatermark?> GetLatestCanonicalAsync(TenantScope scope, ConnectorId connector, string externalArchiveId, CancellationToken cancellationToken);

    /// <summary>Devolve um watermark específico por identidade, dentro do escopo; <see langword="null"/> se inexistente/fora do escopo (anti-IDOR).</summary>
    Task<EvWatermark?> GetByIdAsync(TenantScope scope, WatermarkId id, CancellationToken cancellationToken);
}
