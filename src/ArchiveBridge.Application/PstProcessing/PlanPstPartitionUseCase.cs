using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Application.PstProcessing;

/// <summary>
/// Orquestra o PLANEJAMENTO de particionamento de um PST já sob custódia (Slice 4B, Passo 2). Toda
/// identidade (tenant/projeto/artefato) chega via <see cref="TenantScope"/> resolvido pelo composition root
/// a partir do principal autenticado — NUNCA de um valor fornecido pelo cliente.
/// <para>
/// <b>Read-only por construção:</b> este caso de uso não recebe <see cref="IPstEngine"/> nem qualquer porta
/// capaz de abrir/escrever o PST. Ele lê custódia + inspeção canônica (checkpoints já persistidos no Passo
/// 1), delega a decisão a um <see cref="IPartitionPlanner"/> puro e grava a INTENÇÃO. Nenhum split,
/// rewrite, repair ou PST de saída existe aqui.
/// </para>
/// <para>
/// <b>Idempotência:</b> mesmas entradas + mesma policy/config ⇒ mesmo <c>planHash</c> ⇒ o plano canônico já
/// persistido é devolvido sem gravar nova linha. Mudança de hash de origem ou de policy/config muda o
/// <c>planHash</c> e, portanto, jamais reaproveita o plano anterior. Planos
/// <see cref="PartitionPlanOutcome.Unsupported"/>/<see cref="PartitionPlanOutcome.Blocked"/> nunca são
/// canônicos: são evidência append-only e cada avaliação registra sua própria linha auditável.
/// </para>
/// </summary>
public sealed class PlanPstPartitionUseCase(
    IPstCustodyStore custodyStore,
    IPstInspectionStore inspectionStore,
    IPartitionPlanStore planStore,
    IPartitionPlanner planner,
    IClock clock)
{
    private readonly IPstCustodyStore _custodyStore = custodyStore;
    private readonly IPstInspectionStore _inspectionStore = inspectionStore;
    private readonly IPartitionPlanStore _planStore = planStore;
    private readonly IPartitionPlanner _planner = planner;
    private readonly IClock _clock = clock;

    /// <summary>Calcula (ou reaproveita idempotentemente) o plano de particionamento do artefato.</summary>
    /// <exception cref="PstArtifactNotFoundException">
    /// Artefato inexistente OU pertencente a outro tenant/projeto (indistinguível — anti-IDOR).
    /// </exception>
    /// <exception cref="PstInspectionCanonicityViolationException">
    /// A store de inspeções devolveu como canônico um registro que o Domain não reconhece como tal.
    /// </exception>
    /// <exception cref="PartitionPlanCanonicityViolationException">
    /// A store de planos devolveu como canônico um plano que o Domain não reconhece como tal, ou com
    /// identidade determinística divergente da esperada.
    /// </exception>
    /// <exception cref="PartitionPlanConflictUnresolvedException">
    /// Conflito de gravação sinalizado, mas nenhum plano canônico encontrado na releitura (fail-closed).
    /// </exception>
    public async Task<PartitionPlan> ExecuteAsync(
        TenantScope scope, ArtifactId artifact, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var custody = await _custodyStore.FindAsync(scope, artifact, cancellationToken).ConfigureAwait(false)
            ?? throw new PstArtifactNotFoundException("Artefato PST não encontrado no escopo autorizado.");

        // A inspeção canônica é buscada SEMPRE pelo hash REGISTRADO em custódia: se o artefato mudou desde
        // o registro, não existe canônico para esse hash e o planejamento falha fechado (nunca planeja
        // sobre uma inspeção obsoleta).
        var canonicalInspection = await _inspectionStore
            .FindCanonicalAsync(scope, artifact, custody.RegisteredHash, cancellationToken)
            .ConfigureAwait(false);

        // Defesa em profundidade: a Application nunca confia cegamente que o que veio de FindCanonicalAsync
        // É canônico — revalida o invariante do Domain antes de deixá-lo originar um plano.
        if (canonicalInspection is not null && !canonicalInspection.IsCanonical)
        {
            throw new PstInspectionCanonicityViolationException(
                "IPstInspectionStore.FindCanonicalAsync devolveu um registro que o Domain não reconhece como canônico.");
        }

        var plan = _planner.Plan(custody, canonicalInspection, correlation, _clock.UtcNow);

        // Unsupported/Blocked jamais são canônicos: nunca ocupam o índice único, nunca são reaproveitados e
        // cada avaliação vira sua própria linha de evidência (mesmo padrão de ReadError/Stale no Passo 1).
        if (!plan.IsCanonical)
        {
            return await _planStore.SaveAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        var existing = await FindCanonicalPlanAsync(scope, artifact, plan.PlanHash, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // Réplay idempotente: mesmas entradas + mesma policy/config ⇒ o plano canônico já persistido,
            // sem gravar nova linha e sem efeitos duplicados.
            return existing;
        }

        try
        {
            return await _planStore.SaveAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (PartitionPlanConflictException)
        {
            // Corrida: outra execução concorrente gravou o canônico para o mesmo planHash primeiro.
            // Fail-closed sem duplicar efeitos: relê o canônico persistido. Sua ausência é violação de
            // invariante (quem venceu DEVERIA estar lá), nunca um "sem resultado" silencioso — e jamais
            // devolvemos ao chamador o plano em memória que não foi persistido.
            var raced = await FindCanonicalPlanAsync(scope, artifact, plan.PlanHash, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new PartitionPlanConflictUnresolvedException(
                    "Conflito de gravação detectado (índice único de canonicidade), mas nenhum plano canônico foi encontrado na releitura.");

            return raced;
        }
    }

    private async Task<PartitionPlan?> FindCanonicalPlanAsync(
        TenantScope scope, ArtifactId artifact, Sha256Hash planHash, CancellationToken cancellationToken)
    {
        var existing = await _planStore.FindCanonicalAsync(scope, artifact, planHash, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        // Defesa em profundidade em TRÊS eixos, independentemente da implementação de store: o plano tem de
        // ser canônico para o Domain, ter a identidade PEDIDA e ter uma identidade que se sustenta nas suas
        // próprias entradas (um plan_hash gravado que não é o recalculado nunca é reaproveitado em réplay).
        if (!existing.IsCanonical || existing.PlanHash != planHash || !existing.HasConsistentIdentity())
        {
            throw new PartitionPlanCanonicityViolationException(
                "IPartitionPlanStore.FindCanonicalAsync devolveu um plano que o Domain não reconhece como canônico para esta identidade.");
        }

        return existing;
    }
}
