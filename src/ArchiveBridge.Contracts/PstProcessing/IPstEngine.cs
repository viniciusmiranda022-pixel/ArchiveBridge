using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Resultado normalizado da inspeção de um PST: hash/tamanho REALMENTE observados, diagnóstico estrutural
/// sanitizado e variante de formato — sem tipos do fornecedor. É o único formato que atravessa a fronteira
/// do domínio (§18.2). <see cref="ObservedHash"/>/<see cref="ObservedSizeBytes"/> são <c>null</c> APENAS
/// quando <paramref name="Diagnostic"/> é <see cref="PstStructuralDiagnostic.ReadError"/> (leitura não
/// concluída — sem hash confiável); em qualquer outro diagnóstico o arquivo foi lido por completo e ambos
/// estão presentes. <see cref="ItemCount"/>/<see cref="FolderCount"/> permanecem nulos nesta geração da
/// engine: o mecanismo escolhido para o Passo 1 (ver decisão de adapter) verifica apenas a estrutura do
/// cabeçalho — percorrer a árvore NDB para contagens reais é capacidade de slice/ADR posterior (nenhuma
/// engine de contagem foi aceita em ADR até este Passo). Nunca reportar um valor de contagem inventado.
/// </summary>
public sealed record PstInspectionResult(
    Sha256Hash? ObservedHash,
    long? ObservedSizeBytes,
    PstStructuralDiagnostic Diagnostic,
    PstFormatVariant FormatVariant,
    long? ItemCount,
    int? FolderCount);

/// <summary>
/// Porta da engine PST (§18.2). Nenhum tipo de biblioteca de leitura (Aspose, libpff) cruza esta fronteira
/// — o domínio recebe apenas <see cref="PstInspectionResult"/> normalizado. A validação independente por
/// libpff é capacidade opcional fora do MVP (ADR-0005, <c>BLOCKED_PENDING_EVIDENCE</c>); nenhum binário de
/// fornecedor pode ser distribuído nem invocado por esta implementação.
/// </summary>
public interface IPstEngine
{
    /// <summary>Nome estável do adapter/engine (evidência/auditoria; nunca vaza para o cliente sem revisão).</summary>
    string EngineName { get; }

    /// <summary>Versão do adapter/engine (evidência/auditoria).</summary>
    string EngineVersion { get; }

    /// <summary>
    /// Resolve a identidade/custódia do artefato SERVER-SIDE dentro do <paramref name="scope"/> (nunca a
    /// partir de um caminho fornecido pelo cliente) e inspeciona-o em modo somente leitura. Fail-closed:
    /// arquivo ilegível/inválido/truncado nunca lança exceção não tratada — retorna um
    /// <see cref="PstInspectionResult"/> com o diagnóstico apropriado. Limite de tamanho/tempo excedido
    /// lança <see cref="PstInspectionLimitExceededException"/> (também fail-closed, nunca sucesso falso).
    /// </summary>
    Task<PstInspectionResult> InspectAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken);
}
