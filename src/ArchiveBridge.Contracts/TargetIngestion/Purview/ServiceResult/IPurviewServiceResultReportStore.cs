using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Porta de persistência das versões do validation report / service result do Purview e das suas linhas
/// normalizadas (AB-I6-001 itens 6/9-10). Append-only: uma versão nova nunca sobrescreve/edita uma
/// anterior. O conteúdo bruto É persistido (evidência de custódia, hashado e revalidado a cada leitura —
/// item 9), mas nunca aparece na trilha de auditoria sanitizada (item 11, responsabilidade da Application).
/// </summary>
public interface IPurviewServiceResultReportStore
{
    /// <summary>
    /// A versão deste plano cujo <see cref="PurviewServiceResultReportEvidence.ContentSha256"/> coincide
    /// com <paramref name="contentSha256"/> (<see langword="null"/> se nenhuma) — recupera, sem nova
    /// versão, um relatório com o MESMO conteúdo já persistido (replay idempotente, item 10).
    /// </summary>
    Task<PurviewServiceResultReportEvidence?> GetByContentHashAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, Sha256Hash contentSha256, CancellationToken cancellationToken);

    /// <summary>
    /// Aloca a próxima <see cref="PurviewServiceResultReportEvidence.ReportVersion"/> deste plano sob lock e
    /// persiste, numa única transação curta, os metadados de evidência, o conteúdo bruto (custódia,
    /// hashado) e as linhas normalizadas (filhas, append-only). <paramref name="rawBytes"/> e
    /// <paramref name="rows"/> já foram parseados/correlacionados com sucesso pela Application antes desta
    /// chamada — a store nunca reparseia nem reinterpreta o conteúdo.
    /// </summary>
    Task<PurviewServiceResultReportEvidence> PersistAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        ReadOnlyMemory<byte> rawBytes,
        IReadOnlyList<PurviewServiceResultRow> rows,
        int? declaredTotalRows,
        string uploadedBy,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>A versão mais recente deste plano (<see langword="null"/> se nenhuma foi importada ainda).</summary>
    Task<PurviewServiceResultReportEvidence?> GetLatestAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken);

    /// <summary>
    /// As linhas normalizadas de uma versão específica, revalidadas (fail-closed) contra a evidência
    /// persistida na reidratação (tampering de qualquer linha nunca é devolvido como válido).
    /// </summary>
    /// <exception cref="PurviewServiceResultIntegrityViolationException">Linha(s) adulterada(s) ou hash agregado divergente.</exception>
    Task<IReadOnlyList<PurviewServiceResultRow>> GetRowsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int reportVersion, CancellationToken cancellationToken);
}
