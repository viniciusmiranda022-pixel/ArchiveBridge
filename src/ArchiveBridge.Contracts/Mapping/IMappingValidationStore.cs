using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Contracts.Mapping;

/// <summary>
/// Porta de custódia das TENTATIVAS de validação de upload de CSV de mapping. Conceito distinto de
/// <see cref="IMappingStore"/> (versões GERADAS/artefatos): aqui persiste-se a evidência de RECEPÇÃO —
/// SHA-256 dos bytes exatos, metadados autorizados, snapshot da onda, desfecho e problemas estruturados.
/// Append-only. Toda operação é escopada por tenant/projeto (RLS + filtro explícito) e usa a identidade
/// da aplicação (nunca a de manutenção). A persistência é idempotente pela chave (tenant+projeto+chave)
/// e revalida a onda-fonte na MESMA transação (defesa TOCTOU).
/// </summary>
public interface IMappingValidationStore
{
    /// <summary>
    /// Persiste a tentativa de validação de forma IDEMPOTENTE e ATÔMICA (tentativa + problemas na mesma
    /// transação curta). Comportamento pela chave (tenant+projeto+<see cref="MappingValidationAttempt.IdempotencyKey"/>):
    /// inexistente ⇒ revalida a onda-fonte (versão/hashes/estado Approved|Frozen) e insere (Created);
    /// existente e semanticamente idêntica (mesma onda/versão/hashes/esquema/política/code page/SHA) ⇒
    /// replay do mesmo <c>ValidationId</c> sem novo efeito; existente e divergente ⇒
    /// <see cref="MappingValidationConflictException"/>. Onda-fonte alterada/incompatível no momento do
    /// commit ⇒ <see cref="MappingValidationStaleSourceException"/> (zero efeito).
    /// </summary>
    Task<MappingValidationPersistResult> PersistAsync(MappingValidationAttempt attempt, CancellationToken cancellationToken);

    /// <summary>
    /// Lê uma tentativa persistida (com seus problemas) por identificador, no escopo informado
    /// (<see langword="null"/> se inexistente ou de outro tenant/projeto). Para testes e futuro Portal.
    /// </summary>
    Task<MappingValidationAttempt?> GetAsync(TenantScope scope, Guid validationId, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve a tentativa mais recente (por <see cref="MappingValidationAttempt.CreatedAtUtc"/>) já
    /// persistida neste tenant/projeto — <see langword="null"/> se nenhuma existir. Usado pelo Production
    /// Readiness Review (AB-I8-002), que é escopado a tenant/projeto, não a uma onda específica; nunca filtra
    /// por <see cref="MappingValidationAttempt.WaveId"/>.
    /// </summary>
    Task<MappingValidationAttempt?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken);
}
