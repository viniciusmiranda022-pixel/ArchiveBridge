using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>UM arquivo PST candidato encontrado no diretório de saída — tamanho e SHA-256 já calculados após fechamento (item 10).</summary>
public sealed record EvExportOutputCandidate(string FileNameHint, long SizeBytes, Sha256Hash ContentSha256);

/// <summary>
/// Porta que resolve o <see cref="EvExportOutputLocation"/> para um caminho físico CONTIDO na raiz
/// autorizada do connector (rejeitando traversal/UNC não allowlisted/symlink/junction/reparse point — §15.2,
/// item 9) e varre os arquivos <c>*.pst</c> produzidos, calculando tamanho e SHA-256 EM STREAMING após o
/// fechamento de cada arquivo (item 10) — nunca confia no nome de arquivo como identidade (a identidade
/// canônica é derivada do hash pelo Domain, ver <c>EvExportManifestEntry</c>). Implementada exclusivamente
/// em Infrastructure; Domain/Application nunca tocam um caminho físico.
/// </summary>
public interface IEvExportOutputInspector
{
    /// <summary>
    /// Resolve e varre o diretório de saída. Lança <see cref="Domain.EnterpriseVault.Export.EvExportOutputContainmentException"/>
    /// se o caminho resolvido escapar da raiz autorizada (fail-closed, nenhuma leitura acontece fora da raiz).
    /// </summary>
    Task<IReadOnlyList<EvExportOutputCandidate>> ScanPstOutputsAsync(EvExportOutputLocation location, CancellationToken cancellationToken);
}
