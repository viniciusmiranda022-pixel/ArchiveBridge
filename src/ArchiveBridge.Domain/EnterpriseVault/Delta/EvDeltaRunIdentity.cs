using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Identidade CANÔNICA de UMA execução de fase de delta (AB-4C-008 req 12): computada
/// deterministicamente a partir do escopo, connector, archive-alvo, fase e do watermark ANTERIOR
/// (ausente para Baseline) — deliberadamente SEM a strategy, que é uma decisão DERIVADA (seleção
/// determinística sobre a versão observada), nunca uma entrada do pedido lógico. Duas solicitações com
/// o MESMO phase+watermark+archive convergem SEMPRE para a mesma identidade — e portanto para a mesma
/// operação lógica, mesmo quando a strategy resolvida difere ou é indisponível (bloqueio fail-closed) —
/// nunca duplicando efeitos em retry/concorrência (mesmo padrão de <see cref="Export.EvExportRequestIdentity"/>).
/// </summary>
public readonly record struct EvDeltaRunIdentity
{
    private EvDeltaRunIdentity(Sha256Hash value) => Value = value;

    /// <summary>Hash SHA-256 determinístico da identidade canônica.</summary>
    public Sha256Hash Value { get; }

    /// <summary>Computa a identidade canônica da execução.</summary>
    public static EvDeltaRunIdentity Compute(
        TenantId tenant,
        ProjectId project,
        ConnectorId connector,
        string externalArchiveId,
        EvDeltaPhase phase,
        WatermarkId? previousWatermark)
    {
        if (string.IsNullOrWhiteSpace(externalArchiveId))
        {
            throw new ArgumentException("externalArchiveId é obrigatório.", nameof(externalArchiveId));
        }

        var hash = DeterministicHash.Compute(
        [
            tenant.Value.ToString("N"),
            project.Value.ToString("N"),
            connector.Value.ToString("N"),
            externalArchiveId.Trim(),
            phase.ToString(),
            previousWatermark?.Value.ToString("N") ?? "NONE",
        ]);
        return new EvDeltaRunIdentity(hash);
    }

    /// <summary>
    /// Deriva uma chave de idempotência estável (GUID) a partir do hash canônico — os primeiros 16 bytes do
    /// SHA-256, nunca aleatórios. Usada como restrição de unicidade da execução persistida.
    /// </summary>
    public Guid ToIdempotencyKey()
    {
        var bytes = Convert.FromHexString(Value.Value);
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
