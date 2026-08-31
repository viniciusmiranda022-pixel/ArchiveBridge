using ArchiveBridge.Application.ProductionReadiness;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Application.GoLive;

/// <summary>
/// Revalida FRESCO, no instante da decisão de go-live (AB-I8-010, escopo obrigatório item 4: "revalidar
/// server-side, no momento da autorização, os gates operacionais que podem expirar ou sofrer drift"), o
/// subconjunto Operations/Microsoft365 (§47.4/§47.5) do catálogo do Production Readiness Review (AB-I8-001)
/// — nunca reutiliza o resultado JÁ CACHEADO num <see cref="ProductionReadinessReviewSnapshot"/> antigo, que
/// poderia estar stale mesmo sem ninguém ter recomposto o review. Reaproveita EXATAMENTE os mesmos resolvers
/// canônicos do Passo 1 (<see cref="ReadinessGateEvidenceResolvers"/>, mesmos stores) e a MESMA store de
/// atestação (<see cref="IReadinessControlAttestationStore"/>) — nunca fabrica um mecanismo de evidência
/// paralelo. Nunca chama Purview/Graph/EXO/AzCopy/host/tenant real (STOP-THE-LINE).
/// </summary>
internal static class GoLiveOperationalEvidenceResolvers
{
    /// <summary>Resolve TODOS os controles do subconjunto <see cref="Domain.GoLive.GoLiveGateEvaluator.OperationalControls"/>.</summary>
    public static async Task<Dictionary<ReadinessControlId, ReadinessControlResult>> ResolveAllAsync(
        IRecoveryReadinessStore recoveryReadinessStore,
        IMailboxPrecheckStore mailboxPrecheckStore,
        IMappingValidationStore mappingValidationStore,
        IPurviewUploadAttemptStore uploadAttemptStore,
        AzCopyHomologationCatalog homologatedBinaries,
        IReadinessControlAttestationStore attestationStore,
        TenantScope scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();

        void Add(ReadinessControlResult result) => resolved[result.ControlId] = result;

        // Operations — SystemDerived (mesmos stores/resolvers do Passo 1).
        Add(await ReadinessGateEvidenceResolvers.ResolveRtoAsync(recoveryReadinessStore, scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveRpoAsync(recoveryReadinessStore, scope, now, cancellationToken).ConfigureAwait(false));

        // Microsoft365 — SystemDerived (mesmos stores/resolvers do Passo 1).
        Add(await ReadinessGateEvidenceResolvers.ResolveTenantPrecheckAsync(mailboxPrecheckStore, scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveMappingValidatorAsync(mappingValidationStore, scope, now, cancellationToken).ConfigureAwait(false));
        Add(await ReadinessGateEvidenceResolvers.ResolveAzCopyHomologationAsync(uploadAttemptStore, homologatedBinaries, scope, now, cancellationToken).ConfigureAwait(false));

        // EvidenceUnavailable — sem I/O, mesma resolução determinística fail-closed do Passo 1 (AB-I8-003 blocker 1).
        Add(ReadinessGateEvidenceResolvers.ResolveArchiveLicenseQuota(now));

        // Microsoft365 — SystemDerived por auto-checagem pura (os dois invariantes de policy).
        foreach (var invariantResult in ProductionReadinessPolicyInvariants.Evaluate(now))
        {
            Add(invariantResult);
        }

        // Attested — atestação manual VIGENTE (mesma store do Passo 1) de cada controle Operations/Microsoft365
        // já atestado; ausente permanece NotMeasured por default no avaliador (nunca fabricado aqui).
        var attestations = await attestationStore.GetLatestForAllAsync(scope, cancellationToken).ConfigureAwait(false);
        foreach (var attestation in attestations)
        {
            // Defesa em profundidade: só aceita a atestação se o catálogo AINDA classifica este controle como
            // Attested e pertence ao subconjunto Operations/Microsoft365 (nunca confia cegamente no que foi
            // persistido no passado caso o catálogo mude).
            if (!ReadinessControlCatalog.IsKnown(attestation.ControlId))
            {
                continue;
            }

            var definition = ReadinessControlCatalog.Definition(attestation.ControlId);
            if (definition.EvidenceSource != ReadinessControlEvidenceSource.Attested
                || definition.Group is not (ReadinessGateGroup.Operations or ReadinessGateGroup.Microsoft365))
            {
                continue;
            }

            Add(ReadinessControlResult.Create(
                attestation.ControlId, definition.Group, attestation.Status, attestation.Evidence, attestation.ReasonCode, attestation.SubmittedAtUtc));
        }

        return resolved;
    }
}
