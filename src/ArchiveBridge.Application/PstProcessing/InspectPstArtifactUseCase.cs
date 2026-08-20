using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Application.PstProcessing;

/// <summary>
/// Orquestra a inspeção read-only de um PST já sob custódia (Slice 4B, Passo 1). Toda identidade
/// (tenant/projeto/artefato) chega via <see cref="TenantScope"/> resolvido pelo composition root a partir
/// do principal autenticado — NUNCA de um valor fornecido pelo cliente na requisição. Idempotente: uma
/// tentativa canônica existente (mesmo artefato + mesmo hash de custódia) é devolvida sem reexecutar a
/// engine ("sem duplicar efeitos"). Hash divergente da custódia falha fechado (<c>Stale</c>) e nunca
/// reutiliza um resultado anterior.
/// </summary>
public sealed class InspectPstArtifactUseCase(
    IPstCustodyStore custodyStore,
    IPstInspectionStore inspectionStore,
    IPstEngine engine,
    IClock clock)
{
    private readonly IPstCustodyStore _custodyStore = custodyStore;
    private readonly IPstInspectionStore _inspectionStore = inspectionStore;
    private readonly IPstEngine _engine = engine;
    private readonly IClock _clock = clock;

    /// <summary>
    /// Executa (ou reaproveita idempotentemente) a inspeção do artefato dentro do escopo do chamador.
    /// </summary>
    /// <exception cref="PstArtifactNotFoundException">
    /// Artefato inexistente OU pertencente a outro tenant/projeto (indistinguível — anti-IDOR).
    /// </exception>
    public async Task<PstInspectionRecord> ExecuteAsync(
        TenantScope scope, ArtifactId artifact, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var custody = await _custodyStore.FindAsync(scope, artifact, cancellationToken).ConfigureAwait(false)
            ?? throw new PstArtifactNotFoundException("Artefato PST não encontrado no escopo autorizado.");

        var canonical = await _inspectionStore
            .FindCanonicalAsync(scope, artifact, custody.RegisteredHash, cancellationToken)
            .ConfigureAwait(false);
        if (canonical is not null)
        {
            // Defesa em profundidade: a Application nunca confia cegamente que tudo que a store devolve
            // de FindCanonicalAsync É de fato canônico — revalida o invariante do próprio Domain antes de
            // reaproveitar. Reforça a regra "na Application", não só no SQL (ver AB-4B-002 item 1).
            if (!canonical.IsCanonical)
            {
                throw new PstInspectionCanonicityViolationException(
                    "IPstInspectionStore.FindCanonicalAsync devolveu um registro que o Domain não reconhece como canônico.");
            }

            // Réplay idempotente: mesmo artefato + mesmo hash de custódia ⇒ resultado canônico, sem
            // reinvocar a engine (§4 dos critérios de aceite — "sem duplicar efeitos").
            return canonical;
        }

        var startedAtUtc = _clock.UtcNow;
        var record = await RunEngineAsync(scope, artifact, correlation, custody.RegisteredHash, startedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await _inspectionStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (PstInspectionConflictException)
        {
            // Corrida: outra execução concorrente já gravou o canônico para (artefato, hash) primeiro.
            // Fail-closed sem duplicar efeitos: relê o canônico já persistido em vez de tratar como erro.
            var existing = await _inspectionStore
                .FindCanonicalAsync(scope, artifact, custody.RegisteredHash, cancellationToken)
                .ConfigureAwait(false);

            // Fail-closed (AB-4B-002 item 2): "existing ?? record" devolveria ao chamador um registro NUNCA
            // persistido caso a releitura não encontre nada — mascarando corrupção/condição inesperada como
            // se fosse um checkpoint durável. Se o índice único sinalizou conflito, um canônico DEVE existir;
            // sua ausência é uma violação de invariante, não um "sem resultado" silencioso.
            if (existing is null)
            {
                throw new PstInspectionConflictUnresolvedException(
                    "Conflito de gravação detectado (índice único de canonicidade), mas nenhum resultado canônico foi encontrado na releitura.");
            }

            return existing;
        }
    }

    private async Task<PstInspectionRecord> RunEngineAsync(
        TenantScope scope,
        ArtifactId artifact,
        CorrelationId correlation,
        Sha256Hash expectedHash,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await _engine.InspectAsync(scope, artifact, cancellationToken).ConfigureAwait(false);
            var completedAtUtc = _clock.UtcNow;

            if (output.ObservedHash is not { } observedHash || output.ObservedSizeBytes is not { } observedSizeBytes)
            {
                // Diagnóstico ReadError (ou equivalente sem hash confiável): sempre Completed — não há
                // hash para comparar staleness, mas o diagnóstico em si é definitivo (nunca crash, nunca
                // sucesso falso; ver ArchiveBridge.Domain.PstProcessing.PstStructuralDiagnostic.ReadError).
                return PstInspectionRecord.Complete(
                    InspectionId.New(), scope.Tenant, scope.Project, artifact, expectedHash,
                    observedHash: null, observedSizeBytes: null, output.Diagnostic, output.FormatVariant,
                    _engine.EngineName, _engine.EngineVersion, correlation, startedAtUtc, completedAtUtc);
            }

            return observedHash == expectedHash
                ? PstInspectionRecord.Complete(
                    InspectionId.New(), scope.Tenant, scope.Project, artifact, expectedHash,
                    observedHash, observedSizeBytes, output.Diagnostic, output.FormatVariant,
                    _engine.EngineName, _engine.EngineVersion, correlation, startedAtUtc, completedAtUtc)
                : PstInspectionRecord.Stale(
                    InspectionId.New(), scope.Tenant, scope.Project, artifact, expectedHash,
                    observedHash, observedSizeBytes,
                    _engine.EngineName, _engine.EngineVersion, correlation, startedAtUtc, completedAtUtc);
        }
        catch (PstInspectionLimitExceededException)
        {
            var completedAtUtc = _clock.UtcNow;
            return PstInspectionRecord.LimitExceeded(
                InspectionId.New(), scope.Tenant, scope.Project, artifact, expectedHash, observedSizeBytes: null,
                _engine.EngineName, _engine.EngineVersion, correlation, startedAtUtc, completedAtUtc);
        }
    }
}
