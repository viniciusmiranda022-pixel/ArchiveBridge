using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Mapping;

/// <summary>
/// Desfecho de uma TENTATIVA de validação de upload de CSV de mapping (conceito distinto do
/// <see cref="MappingVersionStatus"/> de uma versão GERADA). <see cref="Valid"/>/<see cref="Invalid"/>
/// aplicam-se a conteúdo integralmente recebido e decodificado; <see cref="Rejected"/> aplica-se a
/// conteúdo integralmente recebido (com SHA-256 calculado) porém impossível de validar semanticamente
/// (ex.: BOM/encoding não-UTF-8). É persistido como <c>TINYINT</c> estável.
/// </summary>
public enum MappingValidationAttemptOutcome : byte
{
    /// <summary>CSV conforme a fonte autorizada.</summary>
    Valid = 0,

    /// <summary>CSV recebido/decodificado, porém não conforme.</summary>
    Invalid = 1,

    /// <summary>CSV recebido (hash calculado), porém não validável semanticamente (encoding/BOM).</summary>
    Rejected = 2,
}

/// <summary>
/// Tentativa DURÁVEL de validação de um upload de CSV de mapping — a evidência de custódia. Vincula o
/// SHA-256 dos BYTES EXATOS recebidos, os metadados autorizados e o snapshot da onda (versão/hashes) ao
/// desfecho e aos problemas estruturados (append-only). NÃO contém bytes brutos, PII, mailbox, caminho,
/// nome de PST nem valor de célula. É append-only e escopada por tenant/projeto.
/// </summary>
public sealed record MappingValidationAttempt(
    Guid ValidationId,
    TenantScope Scope,
    WaveId WaveId,
    int WaveVersion,
    Sha256Hash ConfigurationHash,
    Sha256Hash SelectionHash,
    int MappingSchemaVersion,
    int MappingPolicyVersion,
    ContentCodePage ContentCodePage,
    Sha256Hash ContentSha256,
    long SizeBytes,
    int? RowCount,
    MappingValidationAttemptOutcome Outcome,
    int IssueCount,
    bool IssuesTruncated,
    string? DisplayFileName,
    Guid UserId,
    string RequestedBy,
    CorrelationId Correlation,
    Guid IdempotencyKey,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<MappingValidationIssue> Issues);

/// <summary>
/// Resultado da persistência idempotente de uma tentativa: o identificador durável e se a linha foi
/// criada agora ou reaproveitada (replay) de uma requisição semanticamente idêntica.
/// </summary>
public sealed record MappingValidationPersistResult(Guid ValidationId, bool Created, bool Replayed);
