using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Application.ProductionReadiness;

/// <summary>
/// Comando de composição de UM novo Production Readiness Review (AB-I8-001). O caller fornece SOMENTE
/// identificadores opacos do build sob revisão (item 2, mesmo princípio de
/// <c>IssueReconciliationCertificateCommand</c>) — toda evidência de controle, incluindo policy version e
/// capability matrix, é sempre resolvida server-side no <see cref="TenantScope"/> autorizado a partir dos
/// stores canônicos existentes (AB-I8-002 blocker 1: nenhum fingerprint arbitrário do caller é aceito como
/// evidência canônica — por isso este comando NUNCA carrega <c>PolicyVersionFingerprint</c>/
/// <c>CapabilityMatrixFingerprint</c>; ambos são computados por <see cref="ComposeProductionReadinessReviewUseCase"/>).
/// Nunca carrega ator/papel: identidade e papéis efetivos são sempre resolvidos server-side pelo use case a
/// partir de <see cref="IAuthenticatedActorAccessor"/>.
/// </summary>
public sealed record ComposeProductionReadinessReviewCommand(
    TenantScope Scope,
    string BuildCommitSha,
    Sha256Hash BuildArtifactDigest,
    string ReviewedArtifactName,
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
    ICapabilityEvidenceStore capabilityEvidenceStore,
    IMailboxPrecheckStore mailboxPrecheckStore,
    IMappingValidationStore mappingValidationStore,
    IPurviewUploadAttemptStore uploadAttemptStore,
    AzCopyHomologationCatalog homologatedBinaries,
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
        Add(await ReadinessGateEvidenceResolvers.ResolveTenantPrecheckAsync(mailboxPrecheckStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveMappingValidatorAsync(mappingValidationStore, command.Scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveAzCopyHomologationAsync(
            uploadAttemptStore, homologatedBinaries, command.Scope, now, cancellationToken).ConfigureAwait(false));

        // EvidenceUnavailable — sem I/O, nenhuma fonte canônica existe para este controle (AB-I8-003 blocker
        // 1); resolvido deterministicamente para Blocked, nunca deixado ausente/omitido do dicionário.
        Add(ReadinessGateEvidenceResolvers.ResolveArchiveLicenseQuota(now));

        var capabilityMatrixResult = await ReadinessGateEvidenceResolvers.ResolveCapabilityMatrixAsync(
            capabilityEvidenceStore, command.Scope, now, cancellationToken).ConfigureAwait(false);
        Add(capabilityMatrixResult);

        // SystemDerived por auto-checagem pura (sem I/O) — os dois invariantes de policy M365.
        var policyInvariantResults = ProductionReadinessPolicyInvariants.Evaluate(now);
        foreach (var invariantResult in policyInvariantResults)
        {
            Add(invariantResult);
        }

        // PolicyVersionFingerprint/CapabilityMatrixFingerprint (AB-I8-002 blocker 1) — NUNCA aceitos do
        // caller; sempre resolvidos server-side aqui, a partir de evidência canônica JÁ resolvida acima
        // nesta mesma execução (o fingerprint da capability matrix é o mesmo já computado para o controle
        // ARCH.CAPABILITY_MATRIX_CURRENT — nunca uma segunda leitura/lógica divergente).
        var policyVersionFingerprint = await ReadinessGateEvidenceResolvers.ResolvePolicyVersionFingerprintAsync(
            wdacPolicyStore, command.Scope, policyInvariantResults, cancellationToken).ConfigureAwait(false);
        var capabilityMatrixFingerprint = capabilityMatrixResult.Evidence.Fingerprint;

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
            policyVersionFingerprint,
            capabilityMatrixFingerprint,
            resolved,
            actor,
            role,
            command.Correlation,
            now,
            cancellationToken).ConfigureAwait(false);
    }
}
