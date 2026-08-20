using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Registro de custódia de PSTs sob a raiz configurada no servidor. <see cref="FindAsync"/> é a única fonte
/// de resolução de identidade/caminho: nunca aceita caminho do cliente. Cross-tenant/cross-project retorna
/// <c>null</c> de forma indistinguível de "não existe" (anti-IDOR — nunca revela existência de um artefato
/// de outro escopo). Append-only: um artefato registrado nunca é sobrescrito.
/// </summary>
public interface IPstCustodyStore
{
    /// <summary>Resolve um artefato estritamente dentro do escopo; <c>null</c> se não existir ou pertencer a outro tenant/projeto.</summary>
    Task<MigrationArtifact?> FindAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken);

    /// <summary>Registra um novo artefato de custódia (hash/tamanho observados nesta chamada tornam-se a baseline imutável).</summary>
    Task<MigrationArtifact> RegisterAsync(
        TenantId tenant,
        ProjectId project,
        PstRelativePath relativePath,
        Sha256Hash observedHash,
        long observedSizeBytes,
        CancellationToken cancellationToken);
}
