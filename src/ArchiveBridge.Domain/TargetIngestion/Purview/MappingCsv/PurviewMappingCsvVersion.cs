using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Evidência imutável de uma geração do mapping CSV do Purview (linha da tabela
/// <c>purview_mapping_csv_versions</c>). Reaproveita <see cref="MappingVersion"/> e
/// <see cref="MappingVersionStatus"/> do módulo genérico (item 8 — versionamento monotônico e o protocolo
/// de duas fases reserva→publica→finaliza são idênticos; só a FONTE do conteúdo difere). Liga o artefato à
/// evidência autorizada por hash (impressão digital) e ao responsável e data. Não contém o conteúdo do CSV
/// nem qualquer segredo/PII. Uma nova geração produz N+1 e marca a anterior como
/// <see cref="MappingVersionStatus.Superseded"/> — a evidência antiga é preservada, nunca sobrescrita nem
/// editada manualmente (runbook §25.9 — erro do portal exige nova versão, nunca edição manual do CSV).
/// </summary>
public sealed record PurviewMappingCsvVersion(
    MappingVersion Version,
    ProjectId Project,
    WaveId Wave,
    Sha256Hash ContentSha256,
    int RowCount,
    string GeneratedBy,
    DateTimeOffset CreatedAtUtc,
    MappingVersionStatus Status,
    PurviewMappingGenerationFingerprint Fingerprint,
    string ArtifactPath)
{
    /// <summary>Marca esta versão como substituída (preservada como evidência, não utilizável).</summary>
    public PurviewMappingCsvVersion Supersede() =>
        Status == MappingVersionStatus.Superseded
            ? throw new InvalidOperationException("A versão de mapping do Purview já está marcada como substituída.")
            : this with { Status = MappingVersionStatus.Superseded };
}
