using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Contracts.WavePartitionBindings;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;

namespace ArchiveBridge.Application.WavePartitionBindings;

/// <summary>
/// Pedido de vínculo entre uma onda e um output de particionamento. Carrega SOMENTE identificadores OPACOS
/// (<see cref="Wave"/>/<see cref="Entry"/>/<see cref="Plan"/>/<see cref="Part"/>) — nunca path físico,
/// filename, hash ou tenant/project scope fornecido pelo chamador como autoridade (AB-I5-010 item 3);
/// todos os dados canônicos são reidratados dos stores autorizados por este caso de uso.
/// <see cref="Entry"/> (AB-I5-013 item 3) é a <see cref="WaveEntryId"/> OPACA da entrada planejada
/// (mailbox/PST) à qual o output desta partição pertence — o chamador (operador/ferramenta que já sabe
/// qual artefato de custódia corresponde a qual entrada da seleção) a obtém a partir de uma leitura prévia
/// da onda; este caso de uso nunca a aceita como autoridade final: sempre a RESOLVE contra a seleção
/// corrente da onda e recusa fail-closed uma entrada que não seja membro (item 3 "non-member entry").
/// </summary>
public sealed record CreateWavePartitionOutputBindingRequest(
    TenantScope Scope,
    WaveId Wave,
    WaveEntryId Entry,
    PartitionPlanId Plan,
    PartitionPlanPartId Part,
    CorrelationId Correlation);

/// <summary>
/// Cria (ou converge idempotentemente para) o vínculo canônico entre uma onda existente e uma execução de
/// partição canônica e concluída (AB-I5-010). Nunca aceita path físico, filename, hash, tenant/project
/// scope ou SAS como autoridade — resolve a onda via <see cref="IWaveStore"/> e a execução via
/// <see cref="IPartitionExecutionStore"/>, ambos os únicos stores server-side autorizados, e persiste
/// SOMENTE os IDs opacos reidratados desses stores (item 3).
/// <para>
/// Onda inexistente/fora do escopo e execução inexistente/fora do escopo produzem o MESMO
/// <see cref="WavePartitionOutputBindingSourceNotFoundException"/> — deliberadamente indistinguível
/// (anti-IDOR, mesmo padrão de <c>PurviewArchiveNotFoundException</c>/<c>PartitionPlanNotFoundException</c>).
/// Como <see cref="IPartitionExecutionStore.FindCanonicalAsync"/> só devolve linhas canônicas e VERIFICADAS
/// (Slice 4B: nenhuma tentativa fracassada/pendente é jamais persistida ali), a mera existência do registro
/// já satisfaz "execução concluída e verificada" — não há um segundo enum de status a checar aqui.
/// </para>
/// <para>
/// Idempotência (item 4): um pedido repetido para a MESMA (wave, plano, parte) que resolve para a MESMA
/// execução converge para o vínculo já existente (nenhuma linha duplicada); se a execução canônica mudou
/// (replanejamento produziu um output diferente para o mesmo plano+parte — cenário anômalo, pois
/// <c>IPartitionExecutionStore</c> também é canônico por (plano, parte)), a divergência é recusada
/// fail-closed via <see cref="WavePartitionOutputBindingIncompatibleException"/> — o vínculo NUNCA
/// substitui silenciosamente evidência anterior.
/// </para>
/// </summary>
public sealed class CreateWavePartitionOutputBindingUseCase(
    IWaveStore waves, IPartitionExecutionStore executions, IWavePartitionOutputBindingStore bindings, IClock clock)
{
    private const string SourceNotFoundMessage =
        "Vínculo recusado (fail-closed): onda ou execução de partição canônica inexistente/fora do escopo autorizado.";

    private const int MaxConvergenceAttempts = 8;

    private readonly IWaveStore _waves = waves;
    private readonly IPartitionExecutionStore _executions = executions;
    private readonly IWavePartitionOutputBindingStore _bindings = bindings;
    private readonly IClock _clock = clock;

    /// <summary>Cria (ou converge idempotentemente para) o vínculo.</summary>
    /// <exception cref="WavePartitionOutputBindingSourceNotFoundException">
    /// Onda, entrada (não-membro da seleção corrente) ou execução canônica inexistente/fora do escopo
    /// (causas deliberadamente indistinguíveis).
    /// </exception>
    /// <exception cref="WavePartitionOutputBindingIncompatibleException">
    /// Já existe um vínculo canônico para (wave, plano, parte) apontando para um output ou uma entrada
    /// DIFERENTE, OU o artefato físico desta execução já está canonicamente vinculado a OUTRA entrada
    /// nesta mesma onda (reassignação ambígua entre destinos — AB-I5-013 item 4).
    /// </exception>
    public async Task<WavePartitionOutputBinding> ExecuteAsync(
        CreateWavePartitionOutputBindingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wave = await _waves.GetAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false);
        if (wave is null)
        {
            throw new WavePartitionOutputBindingSourceNotFoundException(SourceNotFoundMessage);
        }

        // Item 3: a entrada é sempre RESOLVIDA server-side contra a seleção CORRENTE da onda — nunca
        // aceita como autoridade só porque o caller a informou. Entrada não-membro é indistinguível de
        // onda/execução inexistente (mesmo padrão anti-IDOR já usado neste caso de uso).
        if (wave.Selection.ResolveEntry(request.Wave, request.Entry) is null)
        {
            throw new WavePartitionOutputBindingSourceNotFoundException(SourceNotFoundMessage);
        }

        var execution = await _executions
            .FindCanonicalAsync(request.Scope, request.Plan, request.Part, cancellationToken)
            .ConfigureAwait(false);
        if (execution is null)
        {
            throw new WavePartitionOutputBindingSourceNotFoundException(SourceNotFoundMessage);
        }

        // Item 4: um artefato físico já canonicamente vinculado a OUTRA entrada, sob (plano, parte)
        // DIFERENTES (ex.: replanejamento do mesmo artefato), nunca é silenciosamente reatribuído a um
        // novo destino — múltiplos vínculos para a MESMA entrada são esperados (um PST grande dividido em
        // várias partes, cada uma com seu próprio (plano, parte)); o que é vedado é o INVERSO: o mesmo
        // artefato aparecendo sob duas entradas de destino distintas.
        var waveBindings = await _bindings.ListForWaveAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false);
        var conflictingArtifactBinding = waveBindings.FirstOrDefault(
            existingBinding => existingBinding.Artifact == execution.Artifact && existingBinding.Entry != request.Entry);
        if (conflictingArtifactBinding is not null)
        {
            throw new WavePartitionOutputBindingIncompatibleException(
                "O artefato físico desta execução já está canonicamente vinculado a uma entrada de destino " +
                "diferente nesta onda — reassignação ambígua recusada (fail-closed).");
        }

        for (var attempt = 0; attempt < MaxConvergenceAttempts; attempt++)
        {
            var existing = await _bindings
                .FindCanonicalAsync(request.Scope, request.Wave, request.Plan, request.Part, cancellationToken)
                .ConfigureAwait(false);

            var candidate = WavePartitionOutputBinding.Create(
                WavePartitionOutputBindingId.New(), request.Scope.Tenant, request.Scope.Project, request.Wave,
                request.Entry, execution, request.Correlation, _clock.UtcNow);

            if (existing is not null)
            {
                if (!existing.IsSameLogicalOutputAs(candidate))
                {
                    throw new WavePartitionOutputBindingIncompatibleException(
                        "Já existe um vínculo canônico para esta onda/plano/parte apontando para um output " +
                        "de particionamento ou uma entrada de destino diferente — a evidência anterior nunca " +
                        "é substituída silenciosamente.");
                }

                return existing;
            }

            try
            {
                return await _bindings.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (WavePartitionOutputBindingConflictException)
            {
                // Corrida de PRIMEIRA criação: outra chamada venceu. Relê o canônico e converge no próximo
                // laço (a nova leitura acima decidirá entre convergência idempotente e conflito real).
            }
        }

        throw new WavePartitionOutputBindingConflictUnresolvedException(
            "Não foi possível convergir o vínculo canônico após múltiplas tentativas concorrentes.");
    }
}
