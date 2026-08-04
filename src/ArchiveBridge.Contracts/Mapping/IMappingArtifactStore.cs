using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Mapping;

/// <summary>Identifica de forma versionada o artefato de uma geração (tenant/projeto/onda/versão).</summary>
public sealed record MappingArtifactDescriptor(TenantScope Scope, WaveId Wave, MappingVersion Version)
{
    /// <summary>
    /// Caminho lógico DETERMINÍSTICO do artefato (relativo à raiz), derivado apenas do escopo/onda/versão.
    /// Fonte única de verdade compartilhada pelo armazenamento de artefatos (destino físico) e pela
    /// reserva no SQL (caminho esperado gravado na versão) — sem duplicar o formato em dois lugares.
    /// </summary>
    public string LogicalPath => string.Create(
        CultureInfo.InvariantCulture,
        $"{Scope.Tenant.Value:N}/{Scope.Project.Value:N}/{Wave.Value:N}/v{Version.Value}");
}

/// <summary>
/// Handle de um artefato ENCENADO (fase 1): os bytes já foram escritos e sincronizados em um diretório
/// temporário, fora de qualquer transação SQL, junto do seu SHA-256. Ainda NÃO publicado.
/// </summary>
public sealed record MappingArtifactStaging(string StagingPath, long SizeBytes, Sha256Hash ContentSha256);

/// <summary>
/// Referência lógica ao artefato imutável publicado: o caminho lógico (versionado, relativo à raiz),
/// o tamanho em bytes e o SHA-256 do conteúdo. É isso — e não os bytes — que o SQL guarda como metadado.
/// </summary>
public sealed record MappingArtifactReference(string LogicalPath, long SizeBytes, Sha256Hash ContentSha256);

/// <summary>Conteúdo lido de volta do artefato imutável (bytes + evidência de integridade), já validado.</summary>
public sealed record MappingArtifactContent(ReadOnlyMemory<byte> Bytes, Sha256Hash ContentSha256);

/// <summary>
/// Porta do armazenamento IMUTÁVEL de artefatos de mapping (baseline on-premises: filesystem local
/// configurável; SMB/NAS opcional; sem dependência de Azure). Protocolo recuperável em DUAS FASES para
/// não manter transação SQL/locks durante o I/O pesado:
/// <list type="number">
/// <item><see cref="StageAsync"/> — escreve <c>mapping.csv</c> + <c>mapping.sha256</c> num diretório de
/// staging temporário, com flush ao disco, FORA de qualquer transação (fase potencialmente longa).</item>
/// <item><see cref="PublishAsync"/> — dentro da transação curta (após atribuir a versão sob lock),
/// escreve o <c>manifest.json</c> no staging e RENOMEIA o diretório inteiro para o destino versionado:
/// os três arquivos aparecem como uma ÚNICA publicação atômica. Nunca sobrescreve uma versão existente
/// (republicação idêntica é idempotente; divergente é recusada).</item>
/// </list>
/// A leitura (<see cref="GetAsync"/>) valida o CONJUNTO (existência dos três arquivos, hash do CSV,
/// conteúdo do sha256, e o manifesto vs. tenant/projeto/onda/versão/tamanho/caminho); inconsistência
/// falha fechada. <b>O CSV contém dados pessoais/metadados de planejamento</b> (Mailbox, FilePath, nome
/// de PST): a raiz de artefatos DEVE ter ACL restritiva (mesmo nível do SQL sob RLS).
/// </summary>
public interface IMappingArtifactStore
{
    /// <summary>Fase 1: encena os bytes num diretório temporário (fora da transação). Retorna o handle.</summary>
    Task<MappingArtifactStaging> StageAsync(
        ReadOnlyMemory<byte> csvBytes, Sha256Hash contentSha256, CancellationToken cancellationToken);

    /// <summary>
    /// Fase 2: publica o conjunto encenado como a versão indicada, de forma atômica e idempotente
    /// (rename do diretório). Não sobrescreve versão existente; republicação idêntica é aceita,
    /// divergente é recusada.
    /// </summary>
    Task<MappingArtifactReference> PublishAsync(
        MappingArtifactStaging staging, MappingArtifactDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// Lê e VALIDA o conjunto publicado da versão descrita; <see langword="null"/> se ausente.
    /// Inconsistência (hash/sha256/manifesto/escopo) falha fechada (não repara silenciosamente).
    /// </summary>
    Task<MappingArtifactContent?> GetAsync(MappingArtifactDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>Descarta (best-effort) um staging não publicado (rollback). Nunca toca versões finais.</summary>
    Task DiscardAsync(MappingArtifactStaging staging, CancellationToken cancellationToken);
}
