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
/// desfecho e aos problemas estruturados (append-only). Escopada por tenant/projeto.
/// <para>
/// CLASSIFICAÇÃO DE DADOS. Este registro CONTÉM metadados operacionais de identidade — <see cref="UserId"/>,
/// <see cref="RequestedBy"/> e <see cref="DisplayFileName"/> (basename informado, sanitizado) — além de
/// hashes e contagens. Portanto NÃO é "zero PII"; é evidência operacional atribuível. O que ele NÃO contém:
/// os bytes brutos do CSV, mailbox, caminho físico, nome de PST ou qualquer valor de célula do mapping.
/// </para>
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
/// Resultado da persistência idempotente de uma tentativa. Carrega a <see cref="Attempt"/> CANÔNICA
/// efetivamente persistida (a tentativa ORIGINAL, em caso de replay — nunca uma recalculada), além de se a
/// linha foi criada agora (<see cref="Created"/>) ou reaproveitada de uma requisição semanticamente idêntica
/// (<see cref="Replayed"/>). Consumidores devem montar sua resposta a partir de <see cref="Attempt"/>, de
/// modo que o mesmo <see cref="ValidationId"/> histórico sempre reflita a evidência persistida — imune a
/// evolução posterior do validador.
/// </summary>
public sealed record MappingValidationPersistResult(MappingValidationAttempt Attempt, bool Created, bool Replayed)
{
    /// <summary>Identificador durável da tentativa canônica (a original, em caso de replay).</summary>
    public Guid ValidationId => Attempt.ValidationId;
}
