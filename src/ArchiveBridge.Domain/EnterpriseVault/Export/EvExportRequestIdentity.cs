using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>
/// Identidade CANÔNICA de um pedido de exportação (AB-4C-005 item 5): computada determinísticamente a
/// partir do escopo, do connector, do archive-alvo e da política EFETIVA — nunca de um valor gerado
/// aleatoriamente pelo cliente. Dois pedidos com o MESMO tenant/projeto/connector/archive/política
/// produzem sempre a mesma identidade; um retry do mesmo pedido lógico converge para a MESMA identidade
/// e, portanto, para a MESMA fila/operação lógica (idempotência por conteúdo, não por token arbitrário).
/// Um pedido com uma política DIFERENTE (ex.: MaxThreads distinto) é, deliberadamente, um pedido lógico
/// diferente — nunca reaproveita o histórico de tentativas de outro envelope de política.
/// </summary>
public readonly record struct EvExportRequestIdentity
{
    private EvExportRequestIdentity(Sha256Hash value) => Value = value;

    /// <summary>Hash SHA-256 determinístico da identidade canônica.</summary>
    public Sha256Hash Value { get; }

    /// <summary>Computa a identidade canônica a partir do escopo, connector, archive e política efetiva.</summary>
    public static EvExportRequestIdentity Compute(
        TenantId tenant, ProjectId project, ConnectorId connector, string externalArchiveId, ExportRequestPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
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
            policy.MaxPstSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            policy.MaxThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);
        return new EvExportRequestIdentity(hash);
    }

    /// <summary>
    /// Deriva uma chave de idempotência estável (GUID) a partir do hash canônico — os primeiros 16 bytes do
    /// SHA-256, nunca aleatórios. Usada como chave de enfileiramento idempotente do comando durável.
    /// </summary>
    public Guid ToIdempotencyKey()
    {
        var bytes = Convert.FromHexString(Value.Value);
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
