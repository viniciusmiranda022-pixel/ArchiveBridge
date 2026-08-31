using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Resolve, para cada cenário <see cref="CanaryScenarioEvidenceSource.SystemDerived"/> do catálogo, o
/// <see cref="CanaryScenarioResult"/> a partir de evidência canônica JÁ PERSISTIDA por incrementos anteriores
/// (I5/I6/I7) — nunca fabrica <see cref="CanaryScenarioStatus.Pass"/> quando o store subjacente não tem
/// registro algum, nunca chama Purview/Graph/EXO/AzCopy/host real (STOP-THE-LINE). Cada leitura passa pelo
/// store canônico (<c>GetLatestAsync</c>), que já revalida tamper-evidence antes de devolver qualquer
/// registro — nenhuma evidência adulterada chega jamais a este resolver.
/// </summary>
internal static class CanaryScenarioEvidenceResolvers
{
    private static readonly CanaryScenarioId TenantMailboxControlledId = new("CANARY.TENANT_MAILBOX_CONTROLLED");
    private static readonly CanaryScenarioId CrashRecoveryId = new("CANARY.CRASH_RECOVERY");
    private static readonly CanaryScenarioId ReconciliationEvidencePackageId = new("CANARY.RECONCILIATION_EVIDENCE_PACKAGE");
    private static readonly CanaryScenarioId RestoreRollbackOperationalId = new("CANARY.RESTORE_ROLLBACK_OPERATIONAL");
    private static readonly CanaryScenarioId ReplaySameTargetRootIdempotentId = new("CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT");
    private static readonly CanaryScenarioId DifferentTargetRootBlocksId = new("CANARY.DIFFERENT_TARGET_ROOT_BLOCKS");
    private static readonly CanaryScenarioId KnownCorruptionQuarantineId = new("CANARY.KNOWN_CORRUPTION_QUARANTINE");
    private static readonly CanaryScenarioId PstSizeBoundaryCoverageId = new("CANARY.PST_SIZE_BOUNDARY_COVERAGE");

    // AB-I8-007: AB-I8-006 tinha inventado 64 MiB/16 GiB (nem um nem outro documentado em lugar algum) —
    // corrigido para usar EXCLUSIVAMENTE autoridade já documentada/existente no repositório, nunca um
    // palpite de engenharia. O runbook (§16.3, §20.1) e o Domain (Slice 4B) já definem o ÚNICO limiar
    // numérico real de "boundary de 18 GB": PartitionPolicy.RunbookTargetPartBytes (18 GiB), o mesmo valor
    // que decide se um PST cabe em uma parte ou exige split. Reutilizado aqui tal como está — sem
    // tolerância/margem implementation-defined: o artefato "boundary" só prova o cenário quando seu
    // ObservedSizeBytes real alcança ou ultrapassa esse limiar exato.
    private static readonly long BoundaryPstMinBytes = PartitionPolicy.RunbookTargetPartBytes;

    /// <summary>CANARY.TENANT_MAILBOX_CONTROLLED — precheck de mailbox mais recente já registrado neste tenant/projeto; ausente ou não Active nunca é Pass.</summary>
    public static async Task<CanaryScenarioResult> ResolveTenantMailboxControlledAsync(
        IMailboxPrecheckStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetLatestAcrossMailboxesAsync(scope, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            // Nunca "nenhuma execução ainda" (Pending): a ausência de QUALQUER precheck significa que o
            // cenário nunca foi de fato exercitado — NotPerformed é o desfecho fail-closed correto aqui.
            return CanaryScenarioResult.Create(
                TenantMailboxControlledId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None,
                "TENANT_MAILBOX_PRECHECK_NOT_PERFORMED", now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(snapshot.SnapshotHash, $"mailbox-precheck:{snapshot.Id.Value}");
        if (snapshot.ArchiveStatus != MailboxArchiveStatus.Active)
        {
            return CanaryScenarioResult.Create(
                TenantMailboxControlledId, CanaryScenarioStatus.Blocked, evidence, "TENANT_MAILBOX_ARCHIVE_NOT_ACTIVE", snapshot.RecordedAtUtc);
        }

        return CanaryScenarioResult.Create(
            TenantMailboxControlledId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, snapshot.RecordedAtUtc);
    }

    /// <summary>CANARY.CRASH_RECOVERY — exercício de reconstrução determinística de trabalho pendente (RecoveryExerciseType.PendingWorkRebuild).</summary>
    public static async Task<CanaryScenarioResult> ResolveCrashRecoveryAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await store.GetLatestAsync(scope, RecoveryExerciseType.PendingWorkRebuild, cancellationToken).ConfigureAwait(false);
        return MapRecoveryRecord(record, CrashRecoveryId, "CRASH_RECOVERY_NOT_EXERCISED", now);
    }

    /// <summary>CANARY.RESTORE_ROLLBACK_OPERATIONAL — restore drill (RecoveryExerciseType.RestoreDrill); permanece Blocked/NotPerformed sem drill comprovado (escopo obrigatório item 9).</summary>
    public static async Task<CanaryScenarioResult> ResolveRestoreRollbackOperationalAsync(
        IRecoveryReadinessStore store, TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await store.GetLatestAsync(scope, RecoveryExerciseType.RestoreDrill, cancellationToken).ConfigureAwait(false);
        return MapRecoveryRecord(record, RestoreRollbackOperationalId, "RESTORE_ROLLBACK_NOT_EXERCISED", now);
    }

    /// <summary>
    /// CANARY.RECONCILIATION_EVIDENCE_PACKAGE — o reconciliation certificate canônico e vigente da onda/job
    /// planejado do canário (escopo obrigatório item 10). <see cref="ReconciliationOutcome.Pass"/>/
    /// <see cref="ReconciliationOutcome.PassWithExplainedExceptions"/> apenas — <c>Inconclusive</c>,
    /// <c>Fail</c> e <c>DuplicateRisk</c> nunca são Pass; ausente é NotPerformed (nenhum reconciliation ainda
    /// emitido para esta onda/job de canário).
    /// </summary>
    public static async Task<CanaryScenarioResult> ResolveReconciliationEvidencePackageAsync(
        IReconciliationCertificateStore store, TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var certificate = await store.GetLatestAsync(scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (certificate is null)
        {
            return CanaryScenarioResult.Create(
                ReconciliationEvidencePackageId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None,
                "RECONCILIATION_CERTIFICATE_NOT_ISSUED", now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(
            certificate.CertificateHash, $"reconciliation-certificate:{wave.Value:N}:{plannedJobName.Value}:v{certificate.CertificateVersion.ToString(CultureInfo.InvariantCulture)}");

        return certificate.Result switch
        {
            ReconciliationOutcome.Pass or ReconciliationOutcome.PassWithExplainedExceptions =>
                CanaryScenarioResult.Create(ReconciliationEvidencePackageId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, certificate.GeneratedAtUtc),
            ReconciliationOutcome.Inconclusive =>
                CanaryScenarioResult.Create(ReconciliationEvidencePackageId, CanaryScenarioStatus.Blocked, evidence, "RECONCILIATION_INCONCLUSIVE", certificate.GeneratedAtUtc),
            _ =>
                CanaryScenarioResult.Create(ReconciliationEvidencePackageId, CanaryScenarioStatus.Fail, evidence, $"RECONCILIATION_{certificate.Result.ToString().ToUpperInvariant()}", certificate.GeneratedAtUtc),
        };
    }

    /// <summary>
    /// CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT (AB-I8-006 reclassificação de OperatorAttested para
    /// SystemDerived) — resolvido a partir da história REAL de tentativas de upload
    /// (<see cref="IPurviewUploadAttemptStore"/>) do pedido canônico da wave. Pass exige DUAS provas
    /// independentes, nunca o status alegado pelo operador: (1) evidência de que o pedido foi de fato
    /// despachado mais de uma vez (réplay real ocorreu — nunca apenas "nunca reexecutado"); (2) apesar
    /// disso, EXATAMENTE UMA tentativa terminou <see cref="PurviewUploadAttemptOutcome.Uploaded"/> (nenhum
    /// efeito externo duplicado — o mesmo invariante que <c>PurviewUploadCommandProcessor</c> aplica no
    /// réplay idempotente precoce, aqui apenas OBSERVADO, nunca reimplementado).
    /// </summary>
    public static async Task<CanaryScenarioResult> ResolveReplaySameTargetRootIdempotentAsync(
        IPurviewUploadRequestStore requestStore, IPurviewUploadAttemptStore attemptStore, TenantScope scope, WaveId wave,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var request = await requestStore.FindCanonicalAsync(scope, wave, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return CanaryScenarioResult.Create(
                ReplaySameTargetRootIdempotentId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None,
                "UPLOAD_REQUEST_NOT_YET_CREATED", now);
        }

        var attempts = await attemptStore.ListAttemptsAsync(scope, request.Id, cancellationToken).ConfigureAwait(false);
        var uploaded = attempts.Where(attempt => attempt.Outcome == PurviewUploadAttemptOutcome.Uploaded).ToList();

        if (uploaded.Count == 0)
        {
            return CanaryScenarioResult.Create(
                ReplaySameTargetRootIdempotentId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None,
                "UPLOAD_NOT_YET_COMPLETED", now);
        }

        var latestUploaded = uploaded[^1];
        var evidence = CanaryEvidenceReference.SystemDerived(
            latestUploaded.IdentityHash,
            $"purview-upload-attempts:{request.Id.Value:N}:attempt={latestUploaded.AttemptNumber.ToString(CultureInfo.InvariantCulture)}:total={attempts.Count.ToString(CultureInfo.InvariantCulture)}");

        if (uploaded.Count > 1)
        {
            // O pedido lógico é 1:1 com a wave PARA SEMPRE e o réplay idempotente precoce do processador
            // real nunca acrescenta uma segunda linha Uploaded (converge sem reexecutar) — mais de uma
            // tentativa Uploaded é, portanto, uma divergência estrutural do invariante de exactly-once, nunca
            // Pass.
            return CanaryScenarioResult.Create(
                ReplaySameTargetRootIdempotentId, CanaryScenarioStatus.Fail, evidence,
                "MULTIPLE_UPLOADED_ATTEMPTS_STRUCTURALLY_UNEXPECTED", latestUploaded.CompletedAtUtc);
        }

        if (attempts.Count < 2 && latestUploaded.AttemptNumber < 2)
        {
            // Uma única tentativa isolada prova apenas "transportado uma vez" — nunca "réplay convergiu sem
            // duplicar efeito" (nenhuma evidência de que o pedido foi de fato despachado de novo). Fail-closed:
            // permanece Blocked até que uma tentativa adicional (retry/reclaim real) seja observada.
            return CanaryScenarioResult.Create(
                ReplaySameTargetRootIdempotentId, CanaryScenarioStatus.Blocked, evidence, "REPLAY_NOT_YET_OBSERVED", latestUploaded.CompletedAtUtc);
        }

        return CanaryScenarioResult.Create(
            ReplaySameTargetRootIdempotentId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, latestUploaded.CompletedAtUtc);
    }

    /// <summary>
    /// CANARY.DIFFERENT_TARGET_ROOT_BLOCKS (AB-I8-006 reclassificação de OperatorAttested para SystemDerived)
    /// — resolvido exercitando o MESMO guard de domínio que protege produção
    /// (<see cref="MigrationWave.ChangeTargetRootFolder"/>, congelado após aprovação) contra um root
    /// candidato DIFERENTE do atual, informado pelo caller. A mutação NUNCA é persistida (a instância
    /// carregada aqui é sempre descartada) — apenas observa deterministicamente se
    /// <see cref="InvalidWaveTransitionException"/> é lançada pelo estado REAL da wave, nunca aceita o
    /// veredito alegado pelo operador.
    /// </summary>
    public static async Task<CanaryScenarioResult> ResolveDifferentTargetRootBlocksAsync(
        IWaveStore waveStore, TenantScope scope, WaveId wave, TargetRootFolder attemptedDifferentRoot, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var loaded = await waveStore.GetAsync(scope, wave, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return CanaryScenarioResult.Create(
                DifferentTargetRootBlocksId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None, "WAVE_NOT_FOUND", now);
        }

        if (string.Equals(loaded.TargetRootFolder.Value, attemptedDifferentRoot.Value, StringComparison.Ordinal))
        {
            // O caller precisa informar um root GENUINAMENTE diferente do atual — sem isso não há nada a
            // provar (o guard nunca é sequer exercitado). Fail-closed: nunca Pass por engano de entrada.
            return CanaryScenarioResult.Create(
                DifferentTargetRootBlocksId, CanaryScenarioStatus.Blocked, CanaryEvidenceReference.None, "ATTEMPTED_ROOT_NOT_ACTUALLY_DIFFERENT", now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(
            DeterministicHash.Compute(
            [
                "archivebridge.canary.target-root-guard.v1", wave.Value.ToString("N"), loaded.TargetRootFolder.Value,
                attemptedDifferentRoot.Value, loaded.Status.ToString(),
            ]),
            $"wave-target-root-guard:{wave.Value:N}:status={loaded.Status}");

        try
        {
            loaded.ChangeTargetRootFolder(attemptedDifferentRoot);
        }
        catch (InvalidWaveTransitionException)
        {
            // O guard REAL de domínio recusou a mutação antes de qualquer persistência/efeito externo.
            return CanaryScenarioResult.Create(DifferentTargetRootBlocksId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, now);
        }

        // A mutação foi aceita: a seleção/destino desta wave ainda está mutável (pré-aprovação) — o guard de
        // congelamento ainda não está em vigor, então não é possível provar bloqueio ainda. Fail-closed:
        // nunca Pass sem a exceção real.
        return CanaryScenarioResult.Create(DifferentTargetRootBlocksId, CanaryScenarioStatus.Blocked, evidence, "WAVE_SELECTION_STILL_MUTABLE", now);
    }

    /// <summary>
    /// CANARY.KNOWN_CORRUPTION_QUARANTINE (AB-I8-006 reclassificação de OperatorAttested para SystemDerived;
    /// corrigido por AB-I8-007) — resolvido a partir de uma <see cref="PstInspectionRecord"/> CANÔNICA já
    /// persistida (<see cref="IPstInspectionStore.FindCanonicalAsync"/>: hash observado bate com o esperado,
    /// então o artefato É genuinamente o esperado, apenas estruturalmente inválido). O §48 item 181 exige
    /// que "corrupção conhecida deve resultar em quarantine" — nenhum mecanismo de quarantine (store,
    /// estado ou ação reforçada) existe hoje neste repositório (grep repo-wide: apenas menções em
    /// prosa/comentário). AB-I8-006 havia estreitado silenciosamente o SIGNIFICADO do cenário para "nunca
    /// elegível a transporte" e ainda assim emitido <see cref="CanaryScenarioStatus.Pass"/> — isso viola o
    /// contrato do cenário. Esta versão NUNCA emite Pass: mesmo com corrupção diagnosticada (evidência
    /// canônica anexada), o resultado é <see cref="CanaryScenarioStatus.Blocked"/> até que um mecanismo de
    /// quarantine real exista e possa ser verificado server-side.
    /// </summary>
    public static async Task<CanaryScenarioResult> ResolveKnownCorruptionQuarantineAsync(
        IPstInspectionStore inspectionStore, TenantScope scope, ArtifactId artifact, Sha256Hash expectedHash, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = await inspectionStore.FindCanonicalAsync(scope, artifact, expectedHash, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return CanaryScenarioResult.Create(
                KnownCorruptionQuarantineId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None, "PST_INSPECTION_NOT_PERFORMED", now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(
            DeterministicHash.Compute(
                ["archivebridge.canary.pst-corruption.v1", record.Id.Value.ToString("N"), record.ExpectedHash.Value, record.Diagnostic?.ToString() ?? "none"]),
            $"pst-inspection:{record.Id.Value:N}:diagnostic={record.Diagnostic?.ToString() ?? "none"}");

        if (record.Diagnostic is null or PstStructuralDiagnostic.Valid)
        {
            // Este artefato canônico não está, de fato, diagnosticado como corrupto — não é possível provar
            // o cenário de corrupção conhecida com ele. Fail-closed: nunca Pass sem diagnóstico real != Valid.
            return CanaryScenarioResult.Create(
                KnownCorruptionQuarantineId, CanaryScenarioStatus.Blocked, evidence, "PST_NOT_DIAGNOSED_CORRUPT", record.CompletedAtUtc);
        }

        // A corrupção FOI diagnosticada server-side (evidência canônica acima) — mas o §48 item 181 exige
        // quarantine, não apenas "diagnosticado corrupto", e nenhum mecanismo de quarantine existe para
        // verificar aqui. Nunca estreitar o requisito para emitir Pass silenciosamente: fail-closed até que
        // um mecanismo real exista.
        return CanaryScenarioResult.Create(
            KnownCorruptionQuarantineId, CanaryScenarioStatus.Blocked, evidence, "CORRUPTION_DIAGNOSED_BUT_NO_QUARANTINE_MECHANISM", record.CompletedAtUtc);
    }

    /// <summary>
    /// CANARY.PST_SIZE_BOUNDARY_COVERAGE (AB-I8-006 reclassificação de OperatorAttested para SystemDerived;
    /// limiares corrigidos por AB-I8-007) — resolvido a partir do <c>ObservedSizeBytes</c> REAL de DUAS
    /// <see cref="PstInspectionRecord"/> canônicas já persistidas (o caller informa os dois artefatos
    /// candidatos — pequeno e boundary; o resolver nunca aceita o veredito do caller, apenas os tamanhos
    /// observados). O lado "boundary" é verificado contra <see cref="BoundaryPstMinBytes"/> — o ÚNICO
    /// limiar de 18 GB documentado neste repositório (<c>PartitionPolicy.RunbookTargetPartBytes</c>, runbook
    /// §16.3/§20.1), sem tolerância implementation-defined. O lado "pequeno" NÃO tem limiar numérico
    /// documentado em lugar algum (runbook, AB-I8-004, ADR ou código-fonte) — AB-I8-006 havia inventado um
    /// (64 MiB), o que AB-I8-007 rejeitou; esta versão nunca inventa um substituto e, por isso, este cenário
    /// permanece estruturalmente <see cref="CanaryScenarioStatus.Blocked"/> até que um critério documentado
    /// para "PST pequeno" exista — nunca Pass por aproximação de engenharia.
    /// </summary>
    public static async Task<CanaryScenarioResult> ResolvePstSizeBoundaryCoverageAsync(
        IPstInspectionStore inspectionStore, TenantScope scope,
        ArtifactId smallArtifact, Sha256Hash smallExpectedHash, ArtifactId boundaryArtifact, Sha256Hash boundaryExpectedHash,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var small = await inspectionStore.FindCanonicalAsync(scope, smallArtifact, smallExpectedHash, cancellationToken).ConfigureAwait(false);
        var boundary = await inspectionStore.FindCanonicalAsync(scope, boundaryArtifact, boundaryExpectedHash, cancellationToken).ConfigureAwait(false);

        if (small is null || boundary is null)
        {
            return CanaryScenarioResult.Create(
                PstSizeBoundaryCoverageId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None,
                small is null ? "SMALL_ARTIFACT_INSPECTION_MISSING" : "BOUNDARY_ARTIFACT_INSPECTION_MISSING", now);
        }

        var observedAt = small.CompletedAtUtc > boundary.CompletedAtUtc ? small.CompletedAtUtc : boundary.CompletedAtUtc;
        var evidence = CanaryEvidenceReference.SystemDerived(
            DeterministicHash.Compute(
            [
                "archivebridge.canary.pst-size-boundary.v1", small.Id.Value.ToString("N"), boundary.Id.Value.ToString("N"),
                (small.ObservedSizeBytes ?? 0).ToString(CultureInfo.InvariantCulture), (boundary.ObservedSizeBytes ?? 0).ToString(CultureInfo.InvariantCulture),
            ]),
            $"pst-size-boundary:small={small.Id.Value:N}:boundary={boundary.Id.Value:N}");

        if (boundary.ObservedSizeBytes is not { } boundarySize || boundarySize < BoundaryPstMinBytes)
        {
            // Único limiar realmente documentado (18 GiB, PartitionPolicy.RunbookTargetPartBytes) — sem
            // margem/tolerância inventada: 16 GiB (o valor anteriormente aceito) fica abaixo e é Blocked.
            return CanaryScenarioResult.Create(PstSizeBoundaryCoverageId, CanaryScenarioStatus.Blocked, evidence, "BOUNDARY_ARTIFACT_BELOW_THRESHOLD", observedAt);
        }

        if (small.ObservedSizeBytes is null)
        {
            return CanaryScenarioResult.Create(PstSizeBoundaryCoverageId, CanaryScenarioStatus.Blocked, evidence, "SMALL_ARTIFACT_SIZE_UNAVAILABLE", observedAt);
        }

        // O lado "boundary" está genuinamente provado (limiar documentado, sem tolerância inventada) — mas
        // "PST pequeno" não tem nenhum limiar numérico documentado em runbook/ADR/código-fonte para provar o
        // outro lado do item 178. Fail-closed: nunca Pass fabricando um critério que a autoridade documentada
        // não define.
        return CanaryScenarioResult.Create(
            PstSizeBoundaryCoverageId, CanaryScenarioStatus.Blocked, evidence, "SMALL_PST_THRESHOLD_UNDOCUMENTED", observedAt);
    }

    private static CanaryScenarioResult MapRecoveryRecord(
        RecoveryReadinessRecord? record, CanaryScenarioId scenarioId, string missingReasonCode, DateTimeOffset now)
    {
        if (record is null)
        {
            return CanaryScenarioResult.Create(
                scenarioId, CanaryScenarioStatus.NotPerformed, CanaryEvidenceReference.None, missingReasonCode, now);
        }

        var evidence = CanaryEvidenceReference.SystemDerived(
            record.EvidenceFingerprint, $"recovery-readiness:{record.ExerciseType}:v{record.ExerciseVersion.ToString(CultureInfo.InvariantCulture)}");

        return record.Status switch
        {
            RecoveryReadinessStatus.Pass =>
                CanaryScenarioResult.Create(scenarioId, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, record.ExecutedAtUtc),
            RecoveryReadinessStatus.Blocked =>
                CanaryScenarioResult.Create(scenarioId, CanaryScenarioStatus.Blocked, evidence, "RECOVERY_OBJECTIVE_NOT_MET", record.ExecutedAtUtc),
            _ =>
                CanaryScenarioResult.Create(scenarioId, CanaryScenarioStatus.NotPerformed, evidence, missingReasonCode, record.ExecutedAtUtc),
        };
    }
}
