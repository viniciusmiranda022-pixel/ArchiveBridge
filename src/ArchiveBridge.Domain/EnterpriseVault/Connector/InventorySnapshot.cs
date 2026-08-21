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
/// Um <see cref="InventorySnapshot"/> persistido não corresponde à sua própria evidência hashada (AB-4C-003):
/// a persistência é fronteira NÃO CONFIÁVEL — nenhuma linha corrompida ou adulterada é reidratada como
/// canônica. Nunca é lançada por <see cref="InventorySnapshot.Create"/> (dados frescos de uma coleta), apenas
/// por <see cref="InventorySnapshot.Rehydrate"/> (dados lidos de volta da persistência).
/// </summary>
public sealed class InventorySnapshotIntegrityViolationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public InventorySnapshotIntegrityViolationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public InventorySnapshotIntegrityViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public InventorySnapshotIntegrityViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Snapshot IMUTÁVEL e versionado do inventário read-only reportado por um connector (AB-4C-001 critérios
/// 6/7/8). Determinístico: o mesmo conjunto de archives sempre produz o mesmo <see cref="SnapshotHash"/>,
/// independentemente da ordem de coleta — os registros são canonicamente ordenados por
/// <see cref="InventoryArchiveRecord.ExternalArchiveId"/> antes do hash, e IDs duplicados na mesma coleta
/// são recusados ANTES de qualquer hash ser computado. A Application decide se um novo snapshot é
/// necessário (hash divergente do último persistido) ou se o envio é um réplay idempotente sem efeito
/// lógico — este tipo nunca reescreve evidência anterior, apenas descreve UMA coleta imutável.
/// <para>
/// A persistência é uma fronteira NÃO CONFIÁVEL (AB-4C-003, mesmo padrão de <c>PartitionPlan</c> no Slice
/// 4B): <see cref="Create"/> e <see cref="Rehydrate"/> reaplicam o MESMO caminho de canonicalização/
/// deduplicação (<see cref="CanonicalOrder"/>), e <see cref="Rehydrate"/> ainda recomputa
/// <see cref="ComputeHash"/> a partir dos archives filhos realmente carregados e o compara byte-a-byte com
/// o <c>snapshot_hash</c> gravado — uma linha adulterada ou corrompida falha fechado em vez de ser
/// devolvida/reaproveitada em réplay.
/// </para>
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

        var ordered = CanonicalOrder(archives);
        if (FindDuplicateExternalArchiveId(ordered) is { } duplicateId)
        {
            throw new DuplicateInventoryArchiveException(duplicateId);
        }

        return new InventorySnapshot(
            id, connector, tenant, project, version, ComputeHash(ordered), ordered, correlation, collectedAtUtc);
    }

    /// <summary>
    /// Reconstrói um snapshot já persistido (uso exclusivo da camada de persistência). A persistência é uma
    /// fronteira NÃO CONFIÁVEL (AB-4C-003): reaplica o MESMO caminho de canonicalização/deduplicação de
    /// <see cref="Create"/> sobre os <paramref name="archives"/> filhos REALMENTE carregados, valida
    /// <paramref name="archiveCount"/> (o <c>archive_count</c> do header, até então dado morto) contra a
    /// quantidade real de filhos, e recomputa <see cref="ComputeHash"/> comparando-o com o
    /// <paramref name="snapshotHash"/> gravado — só devolve o snapshot como canônico se TUDO fechar contra a
    /// própria evidência persistida. Identidade, versão, correlação e carimbo de tempo são sempre
    /// preservados exatamente como gravados: esta validação nunca reescreve evidência nem "corrige"
    /// corrupção transformando-a silenciosamente em um novo snapshot.
    /// </summary>
    /// <exception cref="InventorySnapshotIntegrityViolationException">
    /// A linha persistida viola um invariante estrutural do agregado (IDs de archive duplicados), o
    /// <c>archive_count</c> gravado não corresponde à quantidade de archives filhos carregados, ou o
    /// <c>snapshot_hash</c> gravado não corresponde ao hash recomputado a partir desses filhos.
    /// </exception>
    public static InventorySnapshot Rehydrate(
        InventorySnapshotId id,
        ConnectorId connector,
        TenantId tenant,
        ProjectId project,
        int version,
        Sha256Hash snapshotHash,
        int archiveCount,
        IReadOnlyList<InventoryArchiveRecord> archives,
        CorrelationId correlation,
        DateTimeOffset collectedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(archives);

        if (version <= 0)
        {
            throw new InventorySnapshotIntegrityViolationException(
                $"Versão persistida ({version}) não é positiva — dado corrompido, não argumento inválido de um chamador.");
        }

        if (archives.Count != archiveCount)
        {
            throw new InventorySnapshotIntegrityViolationException(
                $"O archive_count persistido ({archiveCount}) não corresponde à quantidade de archives filhos " +
                $"carregados ({archives.Count}) para o snapshot {id.Value}.");
        }

        var ordered = CanonicalOrder(archives);
        if (FindDuplicateExternalArchiveId(ordered) is { } duplicateId)
        {
            throw new InventorySnapshotIntegrityViolationException(
                $"Snapshot persistido {id.Value} contém external_archive_id duplicado entre seus filhos: {duplicateId}.");
        }

        var recomputedHash = ComputeHash(ordered);
        if (recomputedHash != snapshotHash)
        {
            throw new InventorySnapshotIntegrityViolationException(
                $"O snapshot_hash persistido para {id.Value} não corresponde ao hash recomputado a partir dos " +
                "archives filhos carregados — evidência possivelmente adulterada ou corrompida.");
        }

        return new InventorySnapshot(
            id, connector, tenant, project, version, snapshotHash, ordered, correlation, collectedAtUtc);
    }

    /// <summary>
    /// Ordena canonicamente por <see cref="InventoryArchiveRecord.ExternalArchiveId"/> — caminho ÚNICO
    /// compartilhado por <see cref="Create"/> e <see cref="Rehydrate"/> (nunca podem divergir sobre o que é
    /// a ordem canônica de um snapshot).
    /// </summary>
    private static InventoryArchiveRecord[] CanonicalOrder(IReadOnlyList<InventoryArchiveRecord> archives) =>
        [.. archives.OrderBy(static a => a.ExternalArchiveId, StringComparer.Ordinal)];

    /// <summary>Devolve o id do primeiro archive duplicado numa lista já canonicamente ordenada, ou <see langword="null"/>.</summary>
    private static string? FindDuplicateExternalArchiveId(InventoryArchiveRecord[] ordered)
    {
        for (var i = 1; i < ordered.Length; i++)
        {
            if (string.Equals(ordered[i - 1].ExternalArchiveId, ordered[i].ExternalArchiveId, StringComparison.Ordinal))
            {
                return ordered[i].ExternalArchiveId;
            }
        }

        return null;
    }

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
