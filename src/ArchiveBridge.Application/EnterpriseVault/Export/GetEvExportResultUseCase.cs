using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Export;

namespace ArchiveBridge.Application.EnterpriseVault.Export;

/// <summary>
/// Caso de uso de LEITURA do resultado de exportação concluído (AB-4C-005 item 13): nunca aceita a mera
/// existência de um attempt <c>Completed</c> persistido como prova de sucesso — REVALIDA os outputs físicos
/// contra o manifesto persistido a cada leitura ("replay"), tal como <c>LocalSinglePartExecutionWriter</c>
/// faz ao reabrir/reinspecionar antes de devolver um resultado já publicado. Arquivo ausente, hash
/// divergente ou conjunto físico inconsistente com o manifesto falham fechado
/// (<see cref="EvExportIntegrityViolationException"/>) em vez de devolver um manifesto que não corresponde
/// mais à realidade do disco.
/// </summary>
public sealed class GetEvExportResultUseCase(
    IEvExportAttemptStore attempts, IEvExportOutputInspector inspector, IEvExportAuditTrail audit, string connectorAuthorizedOutputRoot)
{
    private readonly IEvExportAttemptStore _attempts = attempts;
    private readonly IEvExportOutputInspector _inspector = inspector;
    private readonly IEvExportAuditTrail _audit = audit;
    private readonly string _connectorAuthorizedOutputRoot = connectorAuthorizedOutputRoot;

    /// <summary>
    /// Devolve o manifesto canônico da tentativa concluída mais recente do pedido, revalidado contra o disco;
    /// <see langword="null"/> quando não há tentativa concluída (pedido inexistente/ainda em curso/falhado).
    /// </summary>
    /// <exception cref="EvExportIntegrityViolationException">Outputs físicos divergem do manifesto persistido.</exception>
    public async Task<EvExportManifest?> ExecuteAsync(
        TenantScope scope, ExportRequestId request, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var latest = await _attempts.GetLatestAsync(scope, request, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Outcome != EvExportAttemptOutcome.Completed || latest.Manifest is null)
        {
            return null;
        }

        var descriptor = new EvExportOutputDescriptor(scope, latest.Connector, request, latest.Attempt);
        var location = new EvExportOutputLocation(_connectorAuthorizedOutputRoot, descriptor.RelativeToken);

        IReadOnlyList<EvExportOutputCandidate> candidates;
        try
        {
            candidates = await _inspector.ScanPstOutputsAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch (EvExportOutputContainmentException containment)
        {
            await ReportIntegrityFailureAsync(scope, request, latest.Attempt, correlation, containment.Message, cancellationToken)
                .ConfigureAwait(false);
            throw new EvExportIntegrityViolationException(containment.Message);
        }

        EnsureCandidatesMatchManifest(candidates, latest.Manifest);
        return latest.Manifest;
    }

    private static void EnsureCandidatesMatchManifest(IReadOnlyList<EvExportOutputCandidate> candidates, EvExportManifest manifest)
    {
        if (candidates.Count != manifest.Entries.Count)
        {
            throw new EvExportIntegrityViolationException(
                $"Contagem de outputs físicos ({candidates.Count}) diverge do manifesto persistido ({manifest.Entries.Count}).");
        }

        var expected = manifest.Entries
            .ToDictionary(entry => (entry.ContentSha256.Value, entry.SizeBytes), entry => entry);

        foreach (var candidate in candidates)
        {
            if (!expected.ContainsKey((candidate.ContentSha256.Value, candidate.SizeBytes)))
            {
                throw new EvExportIntegrityViolationException(
                    "Um output físico não corresponde a nenhuma entrada do manifesto persistido (ausente/adulterado).");
            }
        }
    }

    private async Task ReportIntegrityFailureAsync(
        TenantScope scope, ExportRequestId request, ExportAttemptId attempt, CorrelationId correlation, string detail,
        CancellationToken cancellationToken) =>
        await _audit.AppendAsync(
            scope,
            new EvExportAuditEvent(request, attempt, EvExportAuditEventCode.IntegrityFailed, detail, correlation, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
}
