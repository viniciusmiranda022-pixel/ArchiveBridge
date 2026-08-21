using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Application.PstProcessing;

/// <summary>
/// Orquestra a EXECUÇÃO de um plano de particionamento já persistido (Slice 4B, Passo 3). Toda identidade
/// (tenant/projeto) chega via <see cref="TenantScope"/> resolvido pelo composition root a partir do
/// principal autenticado — NUNCA de um valor fornecido pelo cliente.
/// <para>
/// <b>Escopo autorizado:</b> apenas planos <see cref="PartitionPlanOutcome.Planned"/> com
/// <see cref="PartitionPlanReason.SinglePartWithinTarget"/> — exatamente uma parte cobrindo a origem
/// inteira — são elegíveis. Qualquer outro caso (Unsupported/Blocked, plano não canônico, identidade
/// forjada) falha fechado ANTES de qualquer I/O, sem criar output e sem qualquer efeito externo (nem
/// arquivo, nem linha de evidência).
/// </para>
/// <para>
/// <b>Idempotência:</b> uma execução canônica já persistida para o (plano, parte) é devolvida sem
/// reexecutar o writer ("sem duplicar efeitos") — mas somente depois de o <see cref="IPartitionPartVerifier"/>
/// revalidar, em somente leitura, que o bundle físico ainda confere byte-for-byte com o registro persistido
/// (correção AB-4B-007): tampering/corrupção/remoção do output depois que o checkpoint SQL já existe nunca é
/// mascarado por um "sucesso" baseado apenas na linha persistida. A origem é revalidada contra o hash
/// registrado em custódia imediatamente antes de materializar — divergência (origem mudou desde o
/// planejamento) falha fechado antes de tocar o arquivo.
/// </para>
/// </summary>
public sealed class ExecutePartitionPlanUseCase(
    IPstCustodyStore custodyStore,
    IPartitionPlanStore planStore,
    IPartitionExecutionStore executionStore,
    IPartitionPartWriter writer,
    IPartitionPartVerifier verifier,
    IClock clock)
{
    private readonly IPstCustodyStore _custodyStore = custodyStore;
    private readonly IPartitionPlanStore _planStore = planStore;
    private readonly IPartitionExecutionStore _executionStore = executionStore;
    private readonly IPartitionPartWriter _writer = writer;
    private readonly IPartitionPartVerifier _verifier = verifier;
    private readonly IClock _clock = clock;

    /// <summary>Executa (ou reaproveita idempotentemente) a materialização da única parte do plano informado.</summary>
    /// <exception cref="PartitionPlanNotFoundException">
    /// Plano (ou artefato de custódia associado) inexistente OU pertencente a outro tenant/projeto (anti-IDOR).
    /// </exception>
    /// <exception cref="PartitionExecutionNotEligibleException">
    /// O plano não é <see cref="PartitionPlanOutcome.Planned"/>/<see cref="PartitionPlanReason.SinglePartWithinTarget"/>,
    /// não é canônico, tem identidade inconsistente, ou não tem exatamente uma parte cobrindo a origem inteira.
    /// </exception>
    /// <exception cref="PartitionExecutionSourceStaleException">
    /// O hash de custódia atual diverge do hash de origem gravado no plano (artefato mudou desde o planejamento).
    /// </exception>
    /// <exception cref="PartitionExecutionCanonicityViolationException">
    /// A store de execuções devolveu como canônica uma execução que o Domain não reconhece como tal.
    /// </exception>
    /// <exception cref="PartitionExecutionConflictUnresolvedException">
    /// Conflito de gravação sinalizado, mas nenhuma execução canônica encontrada na releitura (fail-closed).
    /// </exception>
    /// <exception cref="PartitionExecutionOutputTamperedException">
    /// Uma execução canônica já persistida existe, mas o <see cref="IPartitionPartVerifier"/> detectou que o
    /// bundle físico correspondente foi removido, adulterado ou diverge do registro persistido.
    /// </exception>
    public async Task<PartitionExecutionRecord> ExecuteAsync(
        TenantScope scope, PartitionPlanId planId, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var plan = await _planStore.FindByIdAsync(scope, planId, cancellationToken).ConfigureAwait(false)
            ?? throw new PartitionPlanNotFoundException("Plano de particionamento não encontrado no escopo autorizado.");

        EnsureEligible(plan);
        var part = plan.Parts[0];

        // Réplay idempotente: uma execução canônica já persistida para este (plano, parte) é devolvida sem
        // reexecutar o writer — nenhum I/O de escrita, nenhum efeito duplicado. Antes de confiar na linha
        // persistida, revalida (somente leitura) que o bundle físico ainda existe e confere byte-for-byte —
        // a persistência sozinha nunca é prova suficiente de que o output ainda está lá e intacto.
        var existing = await FindCanonicalExecutionAsync(scope, plan, part, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await _verifier.VerifyAsync(scope, existing, cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var custody = await _custodyStore.FindAsync(scope, plan.Source.Artifact, cancellationToken).ConfigureAwait(false)
            ?? throw new PartitionPlanNotFoundException("Artefato de custódia do plano não encontrado no escopo autorizado.");

        // Staleness: o plano só pode ser executado se a origem AINDA for a mesma que ele descreve. Fail-closed
        // ANTES de qualquer I/O de cópia — nunca materializa sobre um artefato que mudou desde o planejamento.
        if (custody.RegisteredHash != plan.Source.SourceHash)
        {
            throw new PartitionExecutionSourceStaleException(
                "A origem foi alterada desde o planejamento (hash de custódia diverge do hash registrado no plano).");
        }

        // started_at_utc é definido AQUI, determinística e ANTES de qualquer materialização (work order
        // AB-4B-006 item 7 / correção AB-4B-008): fornecido ao writer via PartitionExecutionContext para que
        // o manifesto físico já nasça com a lineage completa, publicado uma ÚNICA vez, sem escrita mutável
        // pós-publicação. correlation já chega como parâmetro desta chamada.
        var context = new PartitionExecutionContext(correlation, _clock.UtcNow);
        var artifact = await _writer.ExecuteAsync(scope, custody, plan, part, context, cancellationToken).ConfigureAwait(false);

        // A lineage do checkpoint SQL vem SEMPRE do que o writer leu de volta do bundle físico já validado —
        // nunca de valores locais recalculados — para que manifest.json e a linha SQL sejam idênticos por
        // construção (o mesmo vale quando o writer convergiu para um bundle publicado por uma execução
        // anterior/concorrente: a lineage persistida tem de ser a de quem realmente materializou o output).
        var record = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(),
            scope.Tenant,
            scope.Project,
            plan.Source.Artifact,
            plan.Id,
            part.Id,
            plan.PlanHash,
            part.Sequence,
            part.PartKey,
            artifact.SourceHash,
            plan.Source.SourceSizeBytes!.Value,
            artifact.OutputHash,
            artifact.OutputSizeBytes,
            new PartitionExecutorIdentity(_writer.ExecutorName, _writer.ExecutorVersion),
            artifact.Correlation,
            artifact.StartedAtUtc,
            artifact.CompletedAtUtc);

        try
        {
            return await _executionStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (PartitionExecutionConflictException)
        {
            // Corrida: outra execução concorrente do mesmo plano/parte gravou o canônico primeiro.
            // Fail-closed sem duplicar efeitos: relê o canônico persistido. Sua ausência é violação de
            // invariante (quem venceu DEVERIA estar lá), nunca um "sem resultado" silencioso.
            var raced = await FindCanonicalExecutionAsync(scope, plan, part, cancellationToken).ConfigureAwait(false)
                ?? throw new PartitionExecutionConflictUnresolvedException(
                    "Conflito de gravação detectado (índice único de canonicidade), mas nenhuma execução canônica foi encontrada na releitura.");

            // Mesmo invariante do réplay comum: nunca devolve uma execução persistida como canônica sem
            // revalidar fisicamente o bundle correspondente primeiro.
            await _verifier.VerifyAsync(scope, raced, cancellationToken).ConfigureAwait(false);
            return raced;
        }
    }

    /// <summary>
    /// Valida a elegibilidade do plano para execução (defesa em profundidade, independente do que a store
    /// devolveu): precisa ser canônico, com identidade consistente, desfecho/motivo exatamente
    /// <see cref="PartitionPlanReason.SinglePartWithinTarget"/> e exatamente uma parte cobrindo a origem
    /// inteira. Qualquer outro caso é recusado ANTES de qualquer efeito externo.
    /// </summary>
    private static void EnsureEligible(PartitionPlan plan)
    {
        if (!plan.IsCanonical || !plan.HasConsistentIdentity())
        {
            throw new PartitionExecutionNotEligibleException(
                "O plano não é canônico ou sua identidade determinística é inconsistente (fail-closed).");
        }

        if (plan.Reason != PartitionPlanReason.SinglePartWithinTarget)
        {
            throw new PartitionExecutionNotEligibleException(
                "Este Passo só executa planos SinglePartWithinTarget; qualquer outro motivo é recusado sem criar output.");
        }

        if (plan.Parts.Count != 1 || !plan.Parts[0].CoversEntireSource)
        {
            throw new PartitionExecutionNotEligibleException(
                "SinglePartWithinTarget exige exatamente uma parte cobrindo a origem inteira.");
        }
    }

    private async Task<PartitionExecutionRecord?> FindCanonicalExecutionAsync(
        TenantScope scope, PartitionPlan plan, PartitionPlanPart part, CancellationToken cancellationToken)
    {
        var existing = await _executionStore.FindCanonicalAsync(scope, plan.Id, part.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        // Defesa em profundidade, independentemente da implementação de store: a execução devolvida como
        // canônica tem de ter uma identidade que se sustenta nas próprias entradas e corresponder ao MESMO
        // planHash do plano que estamos executando agora.
        if (existing.PlanHash != plan.PlanHash || !existing.HasConsistentIdentity())
        {
            throw new PartitionExecutionCanonicityViolationException(
                "IPartitionExecutionStore.FindCanonicalAsync devolveu uma execução que o Domain não reconhece como canônica para este plano/parte.");
        }

        return existing;
    }
}
