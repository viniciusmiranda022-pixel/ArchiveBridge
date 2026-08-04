using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Contexto durável e VERSIONADO de um comando de descoberta (sem credenciais, sem payload pesado). Fixa
/// no enfileiramento a versão/estado que originou o comando (schema do comando, versão/hash de
/// configuração do projeto e versão da política de descoberta); o worker recusa
/// (<c>STALE_COMMAND_CONTEXT</c>) se divergir na execução. Um retry da MESMA versão continua válido.
/// </summary>
public sealed record EvDiscoveryCommandContext(
    int SchemaVersion,
    int? ExpectedProjectConfigurationVersion,
    Sha256Hash? ExpectedConfigurationHash,
    int DiscoveryPolicyVersion)
{
    /// <summary>Versão corrente do schema do envelope de comando de descoberta. Um schema desconhecido é recusado.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Comando técnico durável <c>DiscoverEvCapabilities</c>: identifica o ambiente a sondar e o solicitante,
/// com contexto versionado. Sem credenciais/segredos. É gravado junto do Job durável (workload
/// <see cref="Workload.EnterpriseVault"/>) de forma atômica na tabela <c>ev_discovery_commands</c>.
/// </summary>
public sealed record EvDiscoveryCommand(
    TenantScope Scope,
    EvEnvironmentId EnvironmentId,
    string SiteName,
    string DirectoryServer,
    string RequestedBy,
    CorrelationId Correlation,
    EvDiscoveryCommandContext Context);

/// <summary>Um comando de descoberta reivindicado: o Job (com época de fencing) e o comando a executar.</summary>
public sealed record ClaimedEvDiscoveryCommand(ClaimedJob Job, EvDiscoveryCommand Command);

/// <summary>
/// Fila durável de comandos de descoberta EV sobre os Jobs do Slice 1, com FILTRO ESPECÍFICO por
/// workload <see cref="Workload.EnterpriseVault"/> (nunca Control genérico). O enfileiramento é ATÔMICO
/// (Job + comando numa transação); a reivindicação é EXCLUSIVA (só Jobs EnterpriseVault com contexto em
/// <c>ev_discovery_commands</c>, via <c>EXISTS</c>, com fencing por época) — um Job sem contexto não vira
/// Processing.
/// </summary>
public interface IEvDiscoveryCommandInbox
{
    /// <summary>Enfileira o comando criando o Job durável (EnterpriseVault) e o contexto atomicamente. Retorna o Job.</summary>
    Task<JobId> EnqueueAsync(EvDiscoveryCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Reivindica o próximo comando de descoberta elegível do escopo (Job EnterpriseVault com contexto) e
    /// carrega o comando; <see langword="null"/> quando não há trabalho.
    /// </summary>
    Task<ClaimedEvDiscoveryCommand?> TryClaimNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken);
}
