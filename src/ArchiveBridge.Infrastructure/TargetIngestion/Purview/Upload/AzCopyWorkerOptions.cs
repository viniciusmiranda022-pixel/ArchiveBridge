namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;

/// <summary>
/// Configuração OPERACIONAL do binário AzCopy homologado (AB-I5-009 item 5) — SEMPRE do servidor
/// (implantação do worker), nunca de uma requisição. <see cref="ExecutablePath"/>/<see cref="DeclaredVersion"/>
/// descrevem o binário que a operação DIZ ter instalado; o SHA-256 real é sempre recomputado a partir dos
/// bytes em disco (nunca confiado à configuração) e conferido contra <see cref="HomologatedSha256Hexes"/>
/// — versão E hash têm de corresponder exatamente a uma entrada homologada.
/// </summary>
public sealed class AzCopyWorkerOptions
{
    /// <summary>Caminho absoluto do executável AzCopy homologado no worker.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Versão declarada do binário instalado (evidência/auditoria — a prova real é o hash).</summary>
    public required string DeclaredVersion { get; init; }

    /// <summary>SHA-256 (hex, minúsculas) homologados para <see cref="DeclaredVersion"/> — ao menos um.</summary>
    public required IReadOnlyList<string> HomologatedSha256Hexes { get; init; }

    /// <summary>Raiz server-side dedicada de logs/plan por tentativa (<c>AZCOPY_LOG_LOCATION</c>/<c>AZCOPY_JOB_PLAN_LOCATION</c>).</summary>
    public required string LogRoot { get; init; }

    /// <summary>Teto de bytes capturados por stream (stdout/stderr) do processo AzCopy antes de abortar.</summary>
    public long MaxOutputBytes { get; init; } = 8L * 1024 * 1024;
}
