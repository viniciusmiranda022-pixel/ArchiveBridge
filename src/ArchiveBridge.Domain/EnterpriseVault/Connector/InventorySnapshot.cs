using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>Identidade de um snapshot de inventário, gerada pelo servidor.</summary>
public readonly record struct InventorySnapshotId(Guid Value)
{
    /// <summary>Gera uma nova identidade de snapshot.</summary>
    public static InventorySnapshotId New() => new(Guid.NewGuid());
}

/// <summary>Status observado de um archive no inventário (nunca conteúdo de mailbox).</summary>
public enum InventoryArchiveStatus
{
    /// <summary>Status não determinado pelo adapter.</summary>
    Unknown,

    /// <summary>Archive ativo no Vault Store.</summary>
    Active,

    /// <summary>Archive inativo/desabilitado no Vault Store.</summary>
    Inactive,
}

/// <summary>
/// Um archive normalizado do inventário read-only (AB-4C-001 critério 7): apenas identidade externa
/// estável e opaca, tipo, vault store (quando permitido) e status — NUNCA conteúdo de item, assunto/corpo,
/// anexo ou credencial. <see cref="CapabilityDiagnostics"/> carrega somente códigos curtos sanitizados
/// (sem PII, sem caminho, sem transcript bruto).
/// </summary>
public sealed record InventoryArchiveRecord
{
    private const int ExternalIdMaxLength = 300;
    private const int ArchiveTypeMaxLength = 50;
    private const int VaultStoreMaxLength = 200;
    private const int DiagnosticCodeMaxLength = 50;
    private const int MaxDiagnostics = 20;

    /// <summary>Cria um registro de archive normalizado — cada campo é sanitizado na FORMA (fail-closed).</summary>
    public InventoryArchiveRecord(
        string externalArchiveId,
        string archiveType,
        string? vaultStoreName,
        InventoryArchiveStatus status,
        IReadOnlyList<string> capabilityDiagnostics)
    {
        ExternalArchiveId = TextValue.Require(externalArchiveId, nameof(externalArchiveId), ExternalIdMaxLength);
        ArchiveType = TextValue.Require(archiveType, nameof(archiveType), ArchiveTypeMaxLength);
        VaultStoreName = string.IsNullOrWhiteSpace(vaultStoreName)
            ? null
            : TextValue.Require(vaultStoreName, nameof(vaultStoreName), VaultStoreMaxLength);
        Status = status;

        ArgumentNullException.ThrowIfNull(capabilityDiagnostics);
        if (capabilityDiagnostics.Count > MaxDiagnostics)
        {
            throw new ArgumentException(
                $"No máximo {MaxDiagnostics} diagnósticos de capacidade por archive.", nameof(capabilityDiagnostics));
        }

        CapabilityDiagnostics =
        [
            .. capabilityDiagnostics.Select(
                static code => TextValue.Require(code, "capabilityDiagnostics", DiagnosticCodeMaxLength)),
        ];
    }

    /// <summary>Identidade externa estável e opaca do archive no Enterprise Vault.</summary>
    public string ExternalArchiveId { get; }

    /// <summary>Tipo de archive (ex.: Mailbox/Journal/PublicFolder) — apenas o rótulo, nunca conteúdo.</summary>
    public string ArchiveType { get; }

    /// <summary>Nome do Vault Store, quando permitido pela política do projeto — pode ser omitido.</summary>
    public string? VaultStoreName { get; }

    /// <summary>Status observado do archive.</summary>
    public InventoryArchiveStatus Status { get; }

    /// <summary>Códigos curtos de diagnóstico de capacidade (sem PII, sem caminho, sem transcript bruto).</summary>
    public IReadOnlyList<string> CapabilityDiagnostics { get; }
}

/// <summary>Dois archives da mesma coleta com o mesmo <see cref="InventoryArchiveRecord.ExternalArchiveId"/> — fail-closed.</summary>
public sealed class DuplicateInventoryArchiveException(string externalArchiveId) : Exception(
    $"External archive id duplicado no mesmo snapshot: {externalArchiveId}.");

/// <summary>
/// Snapshot IMUTÁVEL e versionado do inventário read-only reportado por um connector (AB-4C-001 critérios
/// 6/7/8). Determinístico: o mesmo conjunto de archives sempre produz o mesmo <see cref="SnapshotHash"/>,
/// independentemente da ordem de coleta — os registros são canonicamente ordenados por
/// <see cref="InventoryArchiveRecord.ExternalArchiveId"/> antes do hash, e IDs duplicados na mesma coleta
/// são recusados ANTES de qualquer hash ser computado. A Application decide se um novo snapshot é
/// necessário (hash divergente do último persistido) ou se o envio é um réplay idempotente sem efeito
/// lógico — este tipo nunca reescreve evidência anterior, apenas descreve UMA coleta imutável.
/// </summary>
public sealed class InventorySnapshot
{
    private InventorySnapshot(
        InventorySnapshotId id,
        ConnectorId connector,
        TenantId tenant,
        ProjectId project,
        int version,
        Sha256Hash snapshotHash,
        IReadOnlyList<InventoryArchiveRecord> archives,
        CorrelationId correlation,
        DateTimeOffset collectedAtUtc)
    {
        Id = id;
        Connector = connector;
        Tenant = tenant;
        Project = project;
        Version = version;
        SnapshotHash = snapshotHash;
        Archives = archives;
        Correlation = correlation;
        CollectedAtUtc = collectedAtUtc;
    }

    /// <summary>
    /// Cria um snapshot a partir de uma coleta bruta: ordena canonicamente, recusa IDs de archive
    /// duplicados (fail-closed, antes de computar qualquer hash) e computa o hash determinístico.
    /// </summary>
    /// <exception cref="DuplicateInventoryArchiveException">Dois archives com o mesmo id externo.</exception>
    public static InventorySnapshot Create(
        InventorySnapshotId id,
        ConnectorId connector,
        TenantId tenant,
        ProjectId project,
        int version,
        IReadOnlyList<InventoryArchiveRecord> archives,
        CorrelationId correlation,
        DateTimeOffset collectedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(archives);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "A versão do snapshot deve ser positiva.");
        }

        var ordered = archives.OrderBy(static a => a.ExternalArchiveId, StringComparer.Ordinal).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            if (string.Equals(ordered[i - 1].ExternalArchiveId, ordered[i].ExternalArchiveId, StringComparison.Ordinal))
            {
                throw new DuplicateInventoryArchiveException(ordered[i].ExternalArchiveId);
            }
        }

        return new InventorySnapshot(
            id, connector, tenant, project, version, ComputeHash(ordered), ordered, correlation, collectedAtUtc);
    }

    /// <summary>Reconstrói um snapshot já persistido (uso exclusivo da camada de persistência).</summary>
    public static InventorySnapshot Rehydrate(
        InventorySnapshotId id,
        ConnectorId connector,
        TenantId tenant,
        ProjectId project,
        int version,
        Sha256Hash snapshotHash,
        IReadOnlyList<InventoryArchiveRecord> archives,
        CorrelationId correlation,
        DateTimeOffset collectedAtUtc) =>
        new(id, connector, tenant, project, version, snapshotHash, archives, correlation, collectedAtUtc);

    /// <summary>
    /// Hash determinístico do conjunto de archives (ordem canônica por ExternalArchiveId). Cada registro
    /// vira um token opaco (o hash de seus próprios campos) antes de compor o hash do conjunto — evita
    /// qualquer ambiguidade de concatenação entre registros de tamanho variável (mesma técnica de
    /// composição usada por <c>PartitionPolicy.Fingerprint</c>, aplicada aqui a uma lista).
    /// </summary>
    public static Sha256Hash ComputeHash(IReadOnlyList<InventoryArchiveRecord> orderedArchives)
    {
        var parts = new List<string>
        {
            "archiveCount",
            orderedArchives.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var archive in orderedArchives)
        {
            parts.Add(RecordHash(archive).Value);
        }

        return DeterministicHash.Compute(parts);
    }

    private static Sha256Hash RecordHash(InventoryArchiveRecord archive)
    {
        var parts = new List<string>
        {
            "externalArchiveId", archive.ExternalArchiveId,
            "archiveType", archive.ArchiveType,
            "vaultStoreName", archive.VaultStoreName ?? string.Empty,
            "status", archive.Status.ToString(),
            "diagnosticCount",
            archive.CapabilityDiagnostics.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        parts.AddRange(archive.CapabilityDiagnostics);
        return DeterministicHash.Compute(parts);
    }

    /// <summary>Identidade do snapshot.</summary>
    public InventorySnapshotId Id { get; }

    /// <summary>Connector que reportou o snapshot.</summary>
    public ConnectorId Connector { get; }

    /// <summary>Tenant do escopo (herdado da identidade do connector).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo (herdado da identidade do connector).</summary>
    public ProjectId Project { get; }

    /// <summary>Versão monotônica crescente por connector (1, 2, 3, ...).</summary>
    public int Version { get; }

    /// <summary>Hash determinístico do conjunto de archives.</summary>
    public Sha256Hash SnapshotHash { get; }

    /// <summary>Archives normalizados, em ordem canônica.</summary>
    public IReadOnlyList<InventoryArchiveRecord> Archives { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }

    /// <summary>Instante da coleta (UTC).</summary>
    public DateTimeOffset CollectedAtUtc { get; }
}
