using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Application.Canary;

/// <summary>Comando de resolução dos cenários SystemDerived não escopados a uma onda específica (AB-I8-004).</summary>
public sealed record ResolveCanarySystemEvidenceCommand(TenantScope Scope, int PlanVersion, CorrelationId Correlation);

/// <summary>Resultado agregado da resolução dos três cenários SystemDerived não escopados a onda.</summary>
public sealed record ResolveCanarySystemEvidenceResult(
    CanaryScenarioResult TenantMailboxControlled,
    CanaryScenarioResult CrashRecovery,
    CanaryScenarioResult RestoreRollbackOperational);

/// <summary>
/// Resolve e persiste os cenários <see cref="CanaryScenarioEvidenceSource.SystemDerived"/> do catálogo que
/// NÃO dependem de uma onda/job específico do canário (AB-I8-004): tenant/mailbox controlado, crash recovery
/// e restore/rollback operacional — cada um a partir de evidência canônica JÁ PERSISTIDA por I5/I7 via
/// <see cref="CanaryScenarioEvidenceResolvers"/>. Este use case NUNCA decide Pass/Fail/Blocked por si só,
/// apenas orquestra a leitura de evidência e persiste o resultado, escopado a UMA versão específica e
/// VIGENTE do plano. NUNCA marca canário/go-live/projeto concluído, NUNCA escreve em Purview/EXO/Graph/EV/
/// AzCopy/host real (STOP-THE-LINE).
/// </summary>
public sealed class ResolveCanarySystemEvidenceUseCase(
    IMailboxPrecheckStore mailboxPrecheckStore,
    IRecoveryReadinessStore recoveryReadinessStore,
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<ResolveCanarySystemEvidenceResult> ExecuteAsync(ResolveCanarySystemEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanSubmitEvidence(authenticatedActor.Roles);

        var now = clock.UtcNow;

        var tenantMailbox = await CanaryScenarioEvidenceResolvers
            .ResolveTenantMailboxControlledAsync(mailboxPrecheckStore, command.Scope, now, cancellationToken).ConfigureAwait(false);
        var crashRecovery = await CanaryScenarioEvidenceResolvers
            .ResolveCrashRecoveryAsync(recoveryReadinessStore, command.Scope, now, cancellationToken).ConfigureAwait(false);
        var restoreRollback = await CanaryScenarioEvidenceResolvers
            .ResolveRestoreRollbackOperationalAsync(recoveryReadinessStore, command.Scope, now, cancellationToken).ConfigureAwait(false);

        var persistedTenantMailbox = await PersistAsync(command, tenantMailbox, actor, role, now, cancellationToken).ConfigureAwait(false);
        var persistedCrashRecovery = await PersistAsync(command, crashRecovery, actor, role, now, cancellationToken).ConfigureAwait(false);
        var persistedRestoreRollback = await PersistAsync(command, restoreRollback, actor, role, now, cancellationToken).ConfigureAwait(false);

        return new ResolveCanarySystemEvidenceResult(persistedTenantMailbox, persistedCrashRecovery, persistedRestoreRollback);
    }

    private Task<CanaryScenarioResult> PersistAsync(
        ResolveCanarySystemEvidenceCommand command, CanaryScenarioResult resolved, string actor, string role, DateTimeOffset now, CancellationToken cancellationToken) =>
        resultStore.RecordResultAsync(
            command.Scope, command.PlanVersion, resolved.ScenarioId, resolved.Status, resolved.Evidence, resolved.ReasonCode,
            resolved.ObservedAtUtc, actor, role, command.Correlation, now, cancellationToken);
}
