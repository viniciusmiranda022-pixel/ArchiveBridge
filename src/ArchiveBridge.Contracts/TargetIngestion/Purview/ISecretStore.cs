using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview;

/// <summary>
/// Porta substituível de custódia de segredos on-premises (ADR-0008: perfil inicial nó único/DPAPI; HA
/// de segredos <c>BLOCKED_PENDING_EVIDENCE</c> — work order AB-I5-004 item 6). Nenhum tipo desta
/// interface referencia DPAPI/Windows/Azure Key Vault: Domain/Application/Contracts permanecem
/// independentes do mecanismo concreto (item 1/6) — apenas a Infrastructure implementa.
/// <para>
/// <see cref="AcquireAsync"/> é a ÚNICA operação de leitura do valor bruto — reservada ao boundary do
/// futuro <c>ArchiveBridge-UploadWorker</c> (<see cref="WorkloadIdentity"/>, item 10). Nenhum outro
/// membro desta porta devolve o segredo em texto claro.
/// </para>
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Protege e persiste o segredo sob a identidade dedicada do workload (DPAPI baseline), devolvendo
    /// uma referência OPACA para custódia futura. Fail-closed: nunca aceita persistir em texto claro.
    /// </summary>
    /// <exception cref="SecretStoreUnavailableException">O mecanismo de proteção não está disponível neste ambiente.</exception>
    Task<SecretStoreHandleReference> ProtectAsync(
        TenantScope scope, RedactedSecret secret, CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve o segredo protegido em <paramref name="reference"/> — restrito à <paramref name="requester"/>
    /// autorizada (item 10/11). Implementações devem recusar fail-closed qualquer requester fora do
    /// boundary permitido, sem revelar se a referência existe.
    /// </summary>
    /// <exception cref="SecretStoreUnavailableException">O mecanismo de proteção não está disponível neste ambiente.</exception>
    Task<RedactedSecret> AcquireAsync(
        TenantScope scope,
        SecretStoreHandleReference reference,
        WorkloadIdentity requester,
        CorrelationId correlation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Destrói localmente o material protegido (item 12) — nunca promete revogação remota do SAS no
    /// serviço Microsoft. Idempotente: destruir uma referência já destruída/inexistente não lança.
    /// </summary>
    Task DestroyAsync(
        TenantScope scope, SecretStoreHandleReference reference, CorrelationId correlation, CancellationToken cancellationToken);
}
