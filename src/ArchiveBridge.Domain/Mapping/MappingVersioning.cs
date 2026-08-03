using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>Versão monotônica de um artefato de mapping (começa em 1; uma nova geração cria N+1).</summary>
public readonly record struct MappingVersion(int Value)
{
    /// <summary>Primeira versão.</summary>
    public static MappingVersion Initial => new(1);

    /// <summary>Próxima versão (a geração nunca sobrescreve; cria N+1).</summary>
    public MappingVersion Next() => new(Value + 1);
}

/// <summary>Estado de usabilidade de uma versão de mapping (evidência preservada, nunca apagada).</summary>
public enum MappingVersionStatus
{
    /// <summary>Versão corrente utilizável.</summary>
    Usable,

    /// <summary>Substituída por uma versão posterior; preservada como evidência e não utilizável.</summary>
    Superseded,
}

/// <summary>Resultado sintético da validação registrado na evidência.</summary>
public enum MappingValidationOutcome
{
    /// <summary>Validações passaram.</summary>
    Passed,

    /// <summary>Validações falharam (o CSV não é utilizável).</summary>
    Failed,
}

/// <summary>
/// Evidência imutável de uma geração de mapping (linha da tabela <c>mapping_csv_versions</c>). Liga o
/// artefato à fonte autorizada por hashes (configuração do projeto, seleção da onda e conteúdo do
/// CSV) e ao responsável e data. Não contém o conteúdo do CSV nem qualquer segredo/PII. Uma nova
/// geração produz N+1 e marca a anterior como <see cref="MappingVersionStatus.Superseded"/> — a
/// evidência antiga é preservada, nunca sobrescrita nem editada manualmente.
/// </summary>
public sealed record MappingCsvVersion(
    MappingVersion Version,
    ProjectId Project,
    WaveId Wave,
    Sha256Hash ConfigurationHash,
    Sha256Hash SelectionHash,
    Sha256Hash ContentSha256,
    int RowCount,
    MappingValidationOutcome Validation,
    string GeneratedBy,
    DateTimeOffset CreatedAtUtc,
    MappingVersionStatus Status,
    MappingGenerationFingerprint Fingerprint,
    string ArtifactPath)
{
    /// <summary>Marca esta versão como substituída (preservada como evidência, não utilizável).</summary>
    public MappingCsvVersion Supersede() =>
        Status == MappingVersionStatus.Superseded
            ? throw new InvalidOperationException("A versão de mapping já está marcada como substituída.")
            : this with { Status = MappingVersionStatus.Superseded };
}
