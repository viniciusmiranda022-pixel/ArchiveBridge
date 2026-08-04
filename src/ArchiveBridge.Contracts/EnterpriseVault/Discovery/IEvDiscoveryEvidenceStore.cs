using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>Identifica de forma versionada a evidência de uma descoberta (tenant/projeto/ambiente/versão).</summary>
public sealed record EvDiscoveryEvidenceDescriptor(TenantScope Scope, EvEnvironmentId Environment, int DiscoveryVersion)
{
    /// <summary>Caminho lógico DETERMINÍSTICO da evidência (relativo à raiz), fonte única compartilhada.</summary>
    public string LogicalPath => string.Create(
        CultureInfo.InvariantCulture,
        $"{Scope.Tenant.Value:N}/{Scope.Project.Value:N}/{Environment.Value:N}/v{DiscoveryVersion}");
}

/// <summary>Handle de uma evidência ENCENADA (fase 1): bytes escritos e sincronizados fora do SQL.</summary>
public sealed record EvDiscoveryEvidenceStaging(string StagingPath, long SizeBytes, Sha256Hash ContentSha256);

/// <summary>Referência lógica à evidência imutável publicada (caminho + tamanho + sha256).</summary>
public sealed record EvDiscoveryEvidenceReference(string LogicalPath, long SizeBytes, Sha256Hash ContentSha256);

/// <summary>Conteúdo lido de volta da evidência imutável (bytes + integridade), já validado.</summary>
public sealed record EvDiscoveryEvidenceContent(ReadOnlyMemory<byte> Bytes, Sha256Hash ContentSha256);

/// <summary>
/// Porta do armazenamento IMUTÁVEL de evidência de descoberta (baseline on-premises; SMB/NAS opcional).
/// Mesmo protocolo recuperável em DUAS FASES do Slice 2, para NUNCA manter transação SQL/locks durante
/// o I/O de filesystem: <see cref="StageAsync"/> (fora do SQL) → <see cref="PublishAsync"/> (rename
/// atômico do diretório) → leitura valida o CONJUNTO. A evidência pode conter nomes técnicos de
/// servidores/componentes: é <b>informação sensível de infraestrutura</b> e a raiz exige ACL restritiva.
/// </summary>
public interface IEvDiscoveryEvidenceStore
{
    /// <summary>Fase 1: encena os bytes num diretório temporário (fora da transação). Retorna o handle.</summary>
    Task<EvDiscoveryEvidenceStaging> StageAsync(
        ReadOnlyMemory<byte> evidenceBytes, Sha256Hash contentSha256, CancellationToken cancellationToken);

    /// <summary>
    /// Fase 2: publica o conjunto encenado como a versão indicada, de forma atômica e idempotente. Não
    /// sobrescreve versão existente; republicação idêntica valida o bundle COMPLETO e é aceita, divergente
    /// é recusada (imutável, fail-closed).
    /// </summary>
    Task<EvDiscoveryEvidenceReference> PublishAsync(
        EvDiscoveryEvidenceStaging staging, EvDiscoveryEvidenceDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>Lê e VALIDA o conjunto publicado; <see langword="null"/> se ausente. Inconsistência falha fechada.</summary>
    Task<EvDiscoveryEvidenceContent?> GetAsync(EvDiscoveryEvidenceDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>Descarta (best-effort, guardado sob <c>.staging</c>) um staging não publicado. Nunca toca versões finais.</summary>
    Task DiscardAsync(EvDiscoveryEvidenceStaging staging, CancellationToken cancellationToken);
}
