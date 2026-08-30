using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Application.ProductionReadiness;

/// <summary>
/// Comando de composição de UM novo Production Readiness Review (AB-I8-001). O caller fornece SOMENTE
/// identificadores opacos do build/policy/capability sob revisão (item 2, mesmo princípio de
/// <c>IssueReconciliationCertificateCommand</c>) — toda evidência de controle é sempre resolvida server-side
/// no <see cref="TenantScope"/> autorizado a partir dos stores canônicos existentes. Nunca carrega
/// ator/papel: identidade e papéis efetivos são sempre resolvidos server-side pelo use case a partir de
/// <see cref="IAuthenticatedActorAccessor"/>.
/// </summary>
public sealed record ComposeProductionReadinessReviewCommand(
    TenantScope Scope,
    string BuildCommitSha,
    Sha256Hash BuildArtifactDigest,
    string ReviewedArtifactName,
    Sha256Hash PolicyVersionFingerprint,
    Sha256Hash CapabilityMatrixFingerprint,
    CorrelationId Correlation);

/// <summary>
/// Compõe (ou converge idempotentemente para) a versão VIGENTE do Production Readiness Review de um
/// tenant/projeto (AB-I8-001, runbook §47/escopo obrigatório itens 1-7). Resolve, para CADA controle
/// <see cref="ReadinessControlEvidenceSource.SystemDerived"/> do catálogo, a evidência canônica JÁ
/// PERSISTIDA pelos incrementos anteriores (I6/I7) via <see cref="ReadinessGateEvidenceResolvers"/>; para
/// cada controle <see cref="ReadinessControlEvidenceSource.Attested"/>, a atestação manual vigente (se
/// houver). Delega a agregação PURA para <see cref="ProductionReadinessReviewSnapshot.Compose"/> (que por
/// sua vez executa <see cref="ProductionReadinessGateEvaluator"/>) — este use case NUNCA decide
/// Pass/Fail/Blocked por si só, apenas orquestra a leitura de evidência e persiste o resultado. NUNCA marca
/// canário/go-live/projeto concluído, NUNCA escreve em Purview/EXO/Graph/EV/AzCopy/host real (STOP-THE-LINE).
/// </summary>
public sealed class ComposeProductionReadinessReviewUseCase(
    IPenTestReadinessStore penTestStore,
    IWorkerHardeningBaselineStore workerHardeningStore,
    IWdacPolicyEvidenceStore wdacPolicyStore,
    IIncidentResponseDrillStore incidentResponseStore,
    IBuildProvenanceStore buildProvenanceStore,
    IRecoveryReadinessStore recoveryReadinessStore,
    IReadinessControlAttestationStore attestationStore,
    IProductionReadinessReviewStore reviewStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="ProductionReadinessAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<ProductionReadinessReviewSnapshot> ExecuteAsync(
        ComposeProductionReadinessReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // RBAC SEMPRE antes de qualquer acesso a dado de escopo — identidade/papéis vêm EXCLUSIVAMENTE de
        // IAuthenticatedActorAccessor, nunca do comando (mesmo princípio AB-I6-012).
        var authenticatedActor = actorAccessor.Current;
        var actor = ProductionReadinessAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = ProductionReadinessAuthorization.EnsureCanWrite(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();

        void Add(ReadinessControlResult result) => resolved[result.ControlId] = result;

        // SystemDerived — resolvidos a partir de evidência canônica já existente (I6/I7), nunca alegados.
        Add(await ReadinessGateEvidenceResolvers.ResolvePenTestAsync(penTestStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveWdacDefenderPatchingAsync(
            workerHardeningStore, wdacPolicyStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveSbomAndSignaturesAsync(
            buildProvenanceStore, command.Scope, command.ReviewedArtifactName, command.BuildCommitSha, command.BuildArtifactDigest,
            now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveIncidentResponseAsync(incidentResponseStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveRtoAsync(recoveryReadinessStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveRpoAsync(recoveryReadinessStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveBackupRestoreAsync(recoveryReadinessStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveHashesManifestsLineageAsync(recoveryReadinessStore, command.Scope, now, cancellationToken).ConfigureAwait(false));

        // SystemDerived por auto-checagem pura (sem I/O) — os dois invariantes de policy M365.
        foreach (var invariantResult in ProductionReadinessPolicyInvariants.Evaluate(now))
        {
            Add(invariantResult);
        }

        // Attested — atestação manual vigente de cada controle já atestado; ausente permanece NotMeasured
        // por default no avaliador (nunca fabricado aqui).
        var attestations = await attestationStore.GetLatestForAllAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        foreach (var attestation in attestations)
        {
            // Defesa em profundidade: só aceita a atestação se o catálogo AINDA classifica este controle
            // como Attested (nunca confia cegamente no que foi persistido no passado caso o catálogo mude).
            if (!ReadinessControlCatalog.IsKnown(attestation.ControlId))
            {
                continue;
            }

            var definition = ReadinessControlCatalog.Definition(attestation.ControlId);
            if (definition.EvidenceSource != ReadinessControlEvidenceSource.Attested)
            {
                continue;
            }

            Add(ReadinessControlResult.Create(
                attestation.ControlId, definition.Group, attestation.Status, attestation.Evidence, attestation.ReasonCode, attestation.SubmittedAtUtc));
        }

        return await reviewStore.RecordReviewAsync(
            command.Scope,
            command.BuildCommitSha,
            command.BuildArtifactDigest,
            command.PolicyVersionFingerprint,
            command.CapabilityMatrixFingerprint,
            resolved,
            actor,
            role,
            command.Correlation,
            now,
            cancellationToken).ConfigureAwait(false);
    }
}
