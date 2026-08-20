using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>Identidade opaca de um artefato, gerada pelo servidor no registro de custódia — nunca aceita do cliente.</summary>
public readonly record struct ArtifactId(Guid Value)
{
    /// <summary>Gera uma nova identidade de artefato.</summary>
    public static ArtifactId New() => new(Guid.NewGuid());
}

/// <summary>
/// Registro de custódia de um PST (Slice 4B, Passo 1): a ligação, decidida pelo servidor, entre uma
/// identidade opaca (<see cref="ArtifactId"/>), o escopo tenant/projeto, o caminho relativo à raiz de
/// custódia configurada e o hash/tamanho observados no momento do registro. Imutável — nunca sobrescrito
/// após criado (skeleton original: "hash + tamanho + lineage"; lineage entre artefatos derivados é
/// capacidade de slice posterior, fora deste Passo). O hash/tamanho aqui são a BASELINE contra a qual toda
/// inspeção subsequente verifica staleness (§21/§22 do runbook): qualquer divergência falha fechado.
/// </summary>
public sealed class MigrationArtifact
{
    private MigrationArtifact(
        ArtifactId id,
        TenantId tenant,
        ProjectId project,
        PstRelativePath relativePath,
        Sha256Hash registeredHash,
        long registeredSizeBytes,
        DateTimeOffset registeredAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        RelativePath = relativePath;
        RegisteredHash = registeredHash;
        RegisteredSizeBytes = registeredSizeBytes;
        RegisteredAtUtc = registeredAtUtc;
    }

    /// <summary>Registra um novo artefato de custódia com identidade gerada pelo servidor.</summary>
    /// <exception cref="ArgumentException">Tenant/projeto vazios ou tamanho negativo.</exception>
    public static MigrationArtifact Register(
        TenantId tenant,
        ProjectId project,
        PstRelativePath relativePath,
        Sha256Hash observedHash,
        long observedSizeBytes,
        DateTimeOffset registeredAtUtc)
    {
        if (tenant.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant é obrigatório para registrar custódia.", nameof(tenant));
        }

        if (project.Value == Guid.Empty)
        {
            throw new ArgumentException("Projeto é obrigatório para registrar custódia.", nameof(project));
        }

        if (observedSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedSizeBytes), "Tamanho observado não pode ser negativo.");
        }

        return new MigrationArtifact(
            ArtifactId.New(), tenant, project, relativePath, observedHash, observedSizeBytes, registeredAtUtc);
    }

    /// <summary>Reconstrói um artefato de custódia já persistido (uso exclusivo da camada de persistência).</summary>
    public static MigrationArtifact Rehydrate(
        ArtifactId id,
        TenantId tenant,
        ProjectId project,
        PstRelativePath relativePath,
        Sha256Hash registeredHash,
        long registeredSizeBytes,
        DateTimeOffset registeredAtUtc) =>
        new(id, tenant, project, relativePath, registeredHash, registeredSizeBytes, registeredAtUtc);

    /// <summary>Identidade opaca do artefato.</summary>
    public ArtifactId Id { get; }

    /// <summary>Tenant proprietário (nunca inferido do cliente).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto proprietário (nunca inferido do cliente).</summary>
    public ProjectId Project { get; }

    /// <summary>Caminho relativo à raiz de custódia configurada no servidor.</summary>
    public PstRelativePath RelativePath { get; }

    /// <summary>Hash SHA-256 observado no registro — baseline imutável de staleness.</summary>
    public Sha256Hash RegisteredHash { get; }

    /// <summary>Tamanho em bytes observado no registro.</summary>
    public long RegisteredSizeBytes { get; }

    /// <summary>Instante do registro (UTC).</summary>
    public DateTimeOffset RegisteredAtUtc { get; }
}
