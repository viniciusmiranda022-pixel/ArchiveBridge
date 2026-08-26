using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Contracts.WavePartitionBindings;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Operations;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Upload;

/// <summary>Desfecho do processamento de um pedido de upload durável.</summary>
public enum PurviewUploadCommandOutcome
{
    /// <summary>Tentativa concluída com sucesso (transporte comprovado ou réplay idempotente válido).</summary>
    Completed,

    /// <summary>
    /// Erro terminal (fonte adulterada, binário não homologado) OU orçamento de retry esgotado
    /// (AB-I7-002: uma causa transitória que nunca se resolve — ex.: SAS permanentemente consumido —
    /// converge a Failed em vez de retry indefinido): Job falhado.
    /// </summary>
    Failed,

    /// <summary>Erro transitório (SAS contenção, falha de processo) COM orçamento de retry disponível: nova tentativa agendada.</summary>
    Retried,

    /// <summary>Cercamento perdido: nenhum efeito persistido e a conclusão NÃO é reivindicada.</summary>
    Fenced,
}

/// <summary>Resultado do processamento de um pedido de upload reivindicado.</summary>
public sealed record PurviewUploadCommandExecution(
    JobId Job, PurviewUploadRequestId Request, PurviewUploadCommandOutcome Outcome, JobCommandOutcome JobOutcome);

/// <summary>
/// Consumidor durável do pedido lógico de upload Purview (AB-I5-009): reivindica o próximo Job do workload
/// <see cref="Workload.Upload"/> (job lease/fencing/heartbeat, item 9, mesmo desenho de
/// <c>EvExportCommandProcessor</c>), resolve server-side a wave/bindings/SAS/binário (item 2/3/5),
/// revalida FISICAMENTE cada PST fonte contra o checkpoint canônico do Slice 4B (item 12), executa o
/// transporte AzCopy por arquivo (item 6/7), persiste evidência SANITIZADA append-only (item 8/10/11) e
/// conclui/retenta/falha o Job. Heartbeat PERIÓDICO real durante toda a execução — sua perda cancela a
/// operação sem persistir efeito novo (item 9).
/// </summary>
public sealed class PurviewUploadCommandProcessor(
    IJobStore jobs,
    IJobLeaseManager leases,
    IPurviewUploadRequestStore requests,
    IWaveStore waves,
    IWavePartitionOutputBindingStore bindings,
    IPartitionExecutionStore executions,
    IPartitionPartVerifier verifier,
    IPurviewSasUploadHandleStore sasHandles,
    AcquireSasForUploadUseCase sasAcquisition,
    IAzCopyUploadExecutor azcopy,
    IPurviewUploadAttemptStore attempts,
    AzCopyHomologationCatalog homologatedBinaries,
    TimeSpan azCopyTimeout,
    IClock clock,
    RetryPolicy retryPolicy)
{
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly IJobStore _jobs = jobs;
    private readonly RetryPolicy _retryPolicy = retryPolicy;
    private readonly PlanningHeartbeat _heartbeat = new(leases);
    private readonly IPurviewUploadRequestStore _requests = requests;
    private readonly IWaveStore _waves = waves;
    private readonly IWavePartitionOutputBindingStore _bindings = bindings;
    private readonly IPartitionExecutionStore _executions = executions;
    private readonly IPartitionPartVerifier _verifier = verifier;
    private readonly IPurviewSasUploadHandleStore _sasHandles = sasHandles;
    private readonly AcquireSasForUploadUseCase _sasAcquisition = sasAcquisition;
    private readonly IAzCopyUploadExecutor _azcopy = azcopy;
    private readonly IPurviewUploadAttemptStore _attempts = attempts;
    private readonly AzCopyHomologationCatalog _homologatedBinaries = homologatedBinaries;
    private readonly TimeSpan _azCopyTimeout = azCopyTimeout;
    private readonly IClock _clock = clock;

    /// <summary>Reivindica e processa o próximo pedido de upload; <see langword="null"/> se não houver trabalho.</summary>
    public async Task<PurviewUploadCommandExecution?> ProcessNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var claimedJob = await _jobs
            .TryClaimNextAsync(new ClaimRequest(scope, Workload.Upload, worker, leaseDuration, correlation), cancellationToken)
            .ConfigureAwait(false);
        if (claimedJob is null)
        {
            return null;
        }

        var lease = new LeaseCommand(scope, claimedJob.JobId, worker, claimedJob.Epoch, correlation);
        var fence = new JobFence(scope, claimedJob.JobId, worker, claimedJob.Epoch);

        PurviewUploadAttemptRecord? result = null;
        PurviewUploadRequestId requestId = default;
        try
        {
            var beat = await _heartbeat.RunWhileAsync(
                lease,
                HeartbeatInterval(leaseDuration),
                async token =>
                {
                    var request = await _requests.GetByJobAsync(scope, claimedJob.JobId, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "Job de upload reivindicado sem pedido lógico vinculado — invariante de criação atômica violado.");
                    requestId = request.Id;
                    result = await DispatchAsync(scope, request, claimedJob.AttemptNumber, fence, correlation, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            if (beat.Lost)
            {
                return new PurviewUploadCommandExecution(claimedJob.JobId, requestId, PurviewUploadCommandOutcome.Fenced, beat.LastOutcome);
            }
        }
        catch (FencedOutException)
        {
            return new PurviewUploadCommandExecution(claimedJob.JobId, requestId, PurviewUploadCommandOutcome.Fenced, JobCommandOutcome.FencedOut);
        }
        catch (Exception exception) when (IsTerminal(exception))
        {
            var failed = await _jobs.FailAsync(lease, ErrorCode.Validation, cancellationToken).ConfigureAwait(false);
            return new PurviewUploadCommandExecution(claimedJob.JobId, requestId, OutcomeFor(failed, PurviewUploadCommandOutcome.Failed), failed);
        }
        catch (ConcurrencyException)
        {
            var gate = await JobRetryGate
                .ScheduleRetryOrFailAsync(_jobs, _clock, _retryPolicy, lease, ErrorCode.ConcurrencyLost, RetryBackoff, cancellationToken)
                .ConfigureAwait(false);
            var gatedOutcome = gate.RetryScheduled ? PurviewUploadCommandOutcome.Retried : PurviewUploadCommandOutcome.Failed;
            return new PurviewUploadCommandExecution(claimedJob.JobId, requestId, OutcomeFor(gate.Outcome, gatedOutcome), gate.Outcome);
        }

        return await FinalizeJobAsync(claimedJob.JobId, requestId, lease, result!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PurviewUploadCommandExecution> FinalizeJobAsync(
        JobId jobId, PurviewUploadRequestId requestId, LeaseCommand lease, PurviewUploadAttemptRecord result, CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case PurviewUploadAttemptOutcome.Uploaded:
                var completed = await _jobs.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
                return new PurviewUploadCommandExecution(jobId, requestId, OutcomeFor(completed, PurviewUploadCommandOutcome.Completed), completed);

            case PurviewUploadAttemptOutcome.SourceIntegrityFailed:
            case PurviewUploadAttemptOutcome.BinaryMismatch:
                var errorCode = result.Outcome == PurviewUploadAttemptOutcome.SourceIntegrityFailed
                    ? ErrorCode.ArtifactIntegrity
                    : ErrorCode.Validation;
                var failed = await _jobs.FailAsync(lease, errorCode, cancellationToken).ConfigureAwait(false);
                return new PurviewUploadCommandExecution(jobId, requestId, OutcomeFor(failed, PurviewUploadCommandOutcome.Failed), failed);

            default: // SasDenied (contenção de claim/lease), ProcessFailed (falha transitória do processo AzCopy).
                var gate = await JobRetryGate
                    .ScheduleRetryOrFailAsync(_jobs, _clock, _retryPolicy, lease, ErrorCode.TransientProvider, RetryBackoff, cancellationToken)
                    .ConfigureAwait(false);
                var gatedOutcome = gate.RetryScheduled ? PurviewUploadCommandOutcome.Retried : PurviewUploadCommandOutcome.Failed;
                return new PurviewUploadCommandExecution(jobId, requestId, OutcomeFor(gate.Outcome, gatedOutcome), gate.Outcome);
        }
    }

    // Corpo de execução SOB fencing: resolve server-side, revalida fisicamente, adquire o SAS, transporta e
    // persiste attempt + evidência. NUNCA lança para sinalizar desfecho de negócio — desfechos são sempre um
    // PurviewUploadAttemptRecord explícito, persistido AQUI (sob a MESMA época do fence) antes de retornar.
    private async Task<PurviewUploadAttemptRecord> DispatchAsync(
        TenantScope scope, PurviewUploadRequest request, int attemptNumber, JobFence fence, CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = _clock.UtcNow;
        var attemptId = PurviewUploadAttemptId.New();
        // Identidade "provisória" para tentativas que falham ANTES de todos os componentes da identidade
        // real (item 14) serem conhecidos — nunca colide com uma identidade real (prefixo reservado) e nunca
        // é comparada para decidir réplay (só attempts com Outcome=Uploaded participam dessa comparação).
        var earlyIdentity = new Sha256Hash($"unresolved:{scope.Tenant.Value:N}:{request.Wave.Value:N}:{attemptNumber}");

        async Task<PurviewUploadAttemptRecord> FailAsync(
            PurviewUploadAttemptOutcome outcome, string reason, Sha256Hash identity, int? exitCode = null)
        {
            var record = new PurviewUploadAttemptRecord(
                request.Id, attemptId, attemptNumber, identity, outcome, reason, Evidence: null, exitCode, startedAtUtc, _clock.UtcNow);
            await _attempts.AppendAsync(scope, record, fence, cancellationToken).ConfigureAwait(false);
            return record;
        }

        // (item 2) Onda resolvida server-side, sempre — nunca confia em estado anterior. Só onda com
        // seleção congelada é elegível (mesma regra de RequestPurviewUploadUseCase, revalidada aqui porque
        // o estado pode ter mudado entre a solicitação e a execução).
        var wave = await _waves.GetAsync(scope, request.Wave, cancellationToken).ConfigureAwait(false);
        if (wave is null || wave.Status is not (WaveStatus.Approved or WaveStatus.Frozen))
        {
            return await FailAsync(PurviewUploadAttemptOutcome.SourceIntegrityFailed, "WAVE_NOT_ELIGIBLE", earlyIdentity).ConfigureAwait(false);
        }

        // (item 2) O conjunto canônico de PST parts é resolvido EXCLUSIVAMENTE via o vínculo AB-I5-010 —
        // nunca de WaveSelection/WaveEntry.FilePath (planejamento, nunca prova de custódia física).
        var canonicalBindings = await _bindings.ListForWaveAsync(scope, request.Wave, cancellationToken).ConfigureAwait(false);
        if (canonicalBindings.Count == 0)
        {
            return await FailAsync(PurviewUploadAttemptOutcome.SourceIntegrityFailed, "NO_CANONICAL_BINDINGS", earlyIdentity)
                .ConfigureAwait(false);
        }

        // (item 2/12) Re-resolve E revalida FISICAMENTE cada binding — reutiliza a MESMA validação de
        // bundle/evidência já usada no réplay do Slice 4B; nunca parte de um output stale/adulterado.
        var executionRecords = new List<PartitionExecutionRecord>(canonicalBindings.Count);
        foreach (var binding in canonicalBindings)
        {
            var execution = await _executions.FindCanonicalAsync(scope, binding.Plan, binding.Part, cancellationToken).ConfigureAwait(false);
            if (execution is null || execution.Id != binding.Execution || execution.PartKey != binding.PartKey
                || execution.OutputHash != binding.OutputHash || execution.OutputSizeBytes != binding.OutputSizeBytes)
            {
                return await FailAsync(PurviewUploadAttemptOutcome.SourceIntegrityFailed, "EXECUTION_DIVERGED_FROM_BINDING", earlyIdentity)
                    .ConfigureAwait(false);
            }

            try
            {
                await _verifier.VerifyAsync(scope, execution, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await FailAsync(PurviewUploadAttemptOutcome.SourceIntegrityFailed, exception.GetType().Name, earlyIdentity)
                    .ConfigureAwait(false);
            }

            executionRecords.Add(execution);
        }

        // (item 14) Réplay idempotente PRECOCE: se este pedido lógico já tem uma tentativa Uploaded, o
        // transporte já foi comprovado — converge SEM sequer tentar reexecutar. Verificado ANTES de
        // adquirir o SAS deliberadamente: o handle SAS do Passo 2 é DE USO ÚNICO (Consumed é terminal,
        // nunca retorna a Available) — uma vez que uma tentativa desta wave já o consumiu com sucesso, uma
        // segunda tentativa de aquisição SEMPRE falharia fail-closed, mesmo que fosse apenas um réplay
        // legítimo. Como o pedido lógico é 1:1 com a wave PARA SEMPRE e um Job Completed nunca é
        // reivindicado de novo (filtro de claim exclui estado terminal), este ramo só é alcançável em
        // reexecução defensiva/manual — nunca no fluxo normal de claim — e a wave/bindings são imutáveis
        // após a criação, então nenhuma mudança real de conteúdo poderia ter ocorrido entre tentativas.
        var latest = await _attempts.GetLatestAsync(scope, request.Id, cancellationToken).ConfigureAwait(false);
        if (latest is { Outcome: PurviewUploadAttemptOutcome.Uploaded })
        {
            return latest;
        }

        // (item 5) Binário homologado — validado ANTES de sequer adquirir o SAS (nenhum efeito externo
        // até aqui): versão E hash exatos, nunca versão sozinha.
        AzCopyBinaryIdentity observedBinary;
        try
        {
            observedBinary = await _azcopy.ProbeBinaryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AzCopyBinaryUnavailableException)
        {
            return await FailAsync(PurviewUploadAttemptOutcome.BinaryMismatch, "BINARY_UNAVAILABLE", earlyIdentity).ConfigureAwait(false);
        }

        if (!_homologatedBinaries.IsHomologated(observedBinary))
        {
            return await FailAsync(PurviewUploadAttemptOutcome.BinaryMismatch, "BINARY_NOT_HOMOLOGATED", earlyIdentity).ConfigureAwait(false);
        }

        // (item 3) O SAS é adquirido EXCLUSIVAMENTE pelo fluxo de claim/fencing do Passo 2 — nunca um SAS
        // ad hoc; qualquer causa de negação (handle ausente, expirado, lease de outro adquirente, etc.)
        // produz a MESMA exceção uniforme (anti-IDOR já garantido por AcquireSasForUploadUseCase).
        RedactedSecret sas;
        try
        {
            sas = await _sasAcquisition
                .ExecuteAsync(new AcquireSasForUploadRequest(scope, request.Wave, WorkloadIdentities.UploadWorker, correlation), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PurviewSasAcquisitionDeniedException)
        {
            return await FailAsync(PurviewUploadAttemptOutcome.SasDenied, "SAS_ACQUISITION_DENIED", earlyIdentity).ConfigureAwait(false);
        }

        // Leitura SOMENTE de metadado opaco (id/geração) do handle recém-consumido — nunca do segredo em
        // si (já obtido acima, exatamente uma vez) — para compor a identidade lógica do upload (item 14).
        var consumedHandle = await _sasHandles.GetCanonicalAsync(scope, request.Wave, cancellationToken).ConfigureAwait(false);
        if (consumedHandle is null)
        {
            return await FailAsync(PurviewUploadAttemptOutcome.SasDenied, "SAS_HANDLE_MISSING_AFTER_ACQUIRE", earlyIdentity)
                .ConfigureAwait(false);
        }

        var remotePrefix = PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, request.Wave);
        // Identidade lógica do upload (item 14) — registrada na evidência da tentativa para auditoria e
        // para permitir que uma FUTURA leitura confirme que esta tentativa cobriu exatamente este conjunto
        // de bindings/SAS/binário/destino, mesmo que o réplay em si já tenha convergido acima sem precisar
        // recomputá-la para decidir.
        var identityHash = PurviewUploadRequestIdentity.Compute(
            canonicalBindings, consumedHandle.Id.Value, consumedHandle.Generation, observedBinary, remotePrefix);

        // (item 4/6/7) Transporte real: UM arquivo por parte, sequencialmente, cada um já fisicamente
        // revalidado. Qualquer falha (exit code != 0, timeout, limite de output) interrompe o conjunto
        // inteiro — nunca um sucesso parcial "meio enviado" (item 8: identidade lógica única, sem falso
        // sucesso duplicado).
        // (AB-I5-015 item 1/3) A manifestação por arquivo é construída SOMENTE a partir dos bindings/
        // execuções REALMENTE despachados neste attempt — nunca de input do caller nem de contadores
        // agregados — um item por PST efetivamente transportado, com o MESMO nome remoto usado pelo AzCopy.
        var manifest = new List<PurviewUploadFileManifestItem>(executionRecords.Count);
        foreach (var execution in executionRecords)
        {
            var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence);
            var fileRequest = new AzCopyUploadFileRequest(scope, execution, sas, remotePrefix, remoteName, attemptId, _azCopyTimeout);
            var fileResult = await _azcopy.UploadFileAsync(fileRequest, cancellationToken).ConfigureAwait(false);
            if (fileResult.ExitCode != 0 || fileResult.TimedOut || fileResult.OutputLimitExceeded)
            {
                return await FailAsync(
                    PurviewUploadAttemptOutcome.ProcessFailed,
                    fileResult.TimedOut ? "PROCESS_TIMEOUT" : fileResult.OutputLimitExceeded ? "OUTPUT_LIMIT_EXCEEDED" : "PROCESS_EXIT_NONZERO",
                    identityHash, fileResult.ExitCode).ConfigureAwait(false);
            }

            manifest.Add(new PurviewUploadFileManifestItem(execution.Id, remoteName, execution.OutputHash, execution.OutputSizeBytes));
        }

        // (item 10/13) Evidência SANITIZADA: contadores/identidades já conhecidos server-side, nunca output
        // bruto do processo. Uploaded aqui significa "transporte comprovado" — nunca importação/reconciliação
        // Purview (STOP-THE-LINE), que permanecem estados distintos e fora deste Passo.
        var evidence = new PurviewUploadEvidence(observedBinary, remotePrefix, manifest);
        var success = new PurviewUploadAttemptRecord(
            request.Id, attemptId, attemptNumber, identityHash, PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null,
            evidence, ProcessExitCode: 0, startedAtUtc, _clock.UtcNow);
        await _attempts.AppendAsync(scope, success, fence, cancellationToken).ConfigureAwait(false);
        return success;
    }

    private static TimeSpan HeartbeatInterval(TimeSpan leaseDuration) =>
        leaseDuration > TimeSpan.Zero ? leaseDuration / 3.0 : TimeSpan.FromMilliseconds(1);

    private static PurviewUploadCommandOutcome OutcomeFor(JobCommandOutcome jobOutcome, PurviewUploadCommandOutcome appliedOutcome) =>
        jobOutcome is JobCommandOutcome.Applied or JobCommandOutcome.IdempotentReplay
            ? appliedOutcome
            : PurviewUploadCommandOutcome.Fenced;

    private static bool IsTerminal(Exception exception) =>
        exception is InvalidOperationException or ArgumentException;
}
