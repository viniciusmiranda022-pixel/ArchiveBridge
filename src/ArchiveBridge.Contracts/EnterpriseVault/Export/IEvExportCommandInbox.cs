using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>
/// Comando técnico durável <c>ExportEvArchive</c>: identifica o connector, o archive-alvo e a política
/// EFETIVA (já validada pelo Domain), sem credenciais/segredos. Gravado junto do Job durável (workload
/// <see cref="Workload.EnterpriseVault"/>), atomicamente, em <c>ev_export_commands</c>.
/// </summary>
public sealed record EvExportCommand(
    TenantScope Scope,
    ConnectorId Connector,
    string ExternalArchiveId,
    int MaxThreads,
    int MaxPstSizeMb,
    string RequestedBy,
    CorrelationId Correlation)
{
    /// <summary>Versão corrente do schema do envelope de comando de exportação. Um schema desconhecido é recusado.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Um comando de exportação reivindicado: o Job (com época de fencing), o pedido lógico e o comando a executar.</summary>
public sealed record ClaimedEvExportCommand(ClaimedJob Job, ExportRequestId Request, EvExportCommand Command);

/// <summary>Resultado de um enfileiramento idempotente: o pedido/Job (existente ou recém-criado) e se foi replay.</summary>
public sealed record EvExportEnqueueResult(JobId JobId, ExportRequestId RequestId, bool Created, bool Replayed);

/// <summary>
/// Fila durável de comandos de exportação EV sobre os Jobs do Slice 1, com FILTRO ESPECÍFICO por workload
/// <see cref="Workload.EnterpriseVault"/> (nunca Control genérico) — mesmo padrão de
/// <c>IEvDiscoveryCommandInbox</c>. O enfileiramento é SEMPRE idempotente, pela IDENTIDADE CANÔNICA do
/// pedido (<see cref="EvExportRequestIdentity"/>, AB-4C-005 item 5): dois pedidos com o MESMO
/// tenant/projeto/connector/archive/política convergem para o MESMO <see cref="ExportRequestId"/> e Job —
/// nenhum efeito lógico duplicado, mesmo sob concorrência (o índice único filtrado é o backstop SQL).
/// </summary>
public interface IEvExportCommandInbox
{
    /// <summary>
    /// Enfileira de forma IDEMPOTENTE sob a identidade canônica informada. Procura a chave sob lock dentro
    /// do tenant/projeto; se já existir com o MESMO comando, devolve o pedido/Job existentes (replay) sem
    /// criar nada; se existir com comando DIFERENTE, lança <see cref="EvExportIdempotencyConflictException"/>;
    /// caso contrário cria o pedido + Job + comando atomicamente.
    /// </summary>
    Task<EvExportEnqueueResult> EnqueueIdempotentAsync(
        EvExportCommand command, Guid canonicalIdempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Reivindica o próximo comando de exportação elegível do escopo (Job EnterpriseVault com contexto de
    /// exportação) e carrega o comando; <see langword="null"/> quando não há trabalho.
    /// </summary>
    Task<ClaimedEvExportCommand?> TryClaimNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken);
}
