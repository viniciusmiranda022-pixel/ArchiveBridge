using System.Globalization;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de aprovação da primeira onda real de baixa criticidade (AB-I8-004, escopo obrigatório item 11).
/// Nunca carrega ator/papel: identidade e papéis efetivos são sempre resolvidos server-side pelo use case.
/// </summary>
public sealed record ApproveCanaryFirstWaveCommand(TenantScope Scope, int PlanVersion, string? Notes, CorrelationId Correlation);

/// <summary>
/// Registra a decisão humana auditável de aprovação da primeira onda real de baixa criticidade (runbook §48
/// item 185) — o ÚNICO caminho capaz de produzir um resultado <see cref="CanaryScenarioStatus.Pass"/> para
/// <see cref="CanaryScenarioCatalog.FirstWaveApprovalScenarioId"/>. RECUSA fail-closed, ANTES de registrar
/// qualquer decisão, quando QUALQUER outro cenário obrigatório do plano ainda não está
/// <see cref="CanaryScenarioStatus.Pass"/> (escopo obrigatório item 11: a aprovação nunca pode ser o único
/// cenário "resolvido" do canário). Esta aprovação APENAS autoriza avançar para a etapa de operational
/// readiness/go-live — NUNCA marca projeto/wave <c>COMPLETED</c>, NUNCA declara <c>ProductionReady</c>/
/// <c>GoLive</c> (STOP-THE-LINE).
/// </summary>
public sealed class ApproveCanaryFirstWaveUseCase(
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryFirstWaveApprovalBlockedException">Qualquer outro cenário obrigatório do plano ainda não está Pass.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryScenarioResult> ExecuteAsync(ApproveCanaryFirstWaveCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A aprovação final é a decisão mais sensível deste Passo — restrita a Administrator/Approver, nunca
        // a Operator (que pode submeter evidência de cenário, mas não decidir go-live da primeira onda).
        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanApproveFirstWave(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolvedResults = await resultStore.GetAllLatestForPlanAsync(command.Scope, command.PlanVersion, cancellationToken).ConfigureAwait(false);
        var evaluation = CanaryGateEvaluator.Evaluate(resolvedResults, now);

        // Bloqueio estrutural (escopo obrigatório item 11): ignora o próprio gate de aprovação nesta checagem
        // (ele ainda não foi submetido — é exatamente o que este use case está prestes a fazer) e exige que
        // TODOS os NOVE demais cenários já estejam Pass.
        var otherBlockers = evaluation.Blockers
            .Where(blocker => blocker.ScenarioId != CanaryScenarioCatalog.FirstWaveApprovalScenarioId)
            .ToList();
        if (otherBlockers.Count > 0)
        {
            var summary = string.Join("; ", otherBlockers.Select(blocker => $"{blocker.ScenarioId.Value}={blocker.Status}"));
            throw new CanaryFirstWaveApprovalBlockedException(
                $"A primeira onda real não pode ser aprovada: {otherBlockers.Count} cenário(s) obrigatório(s) ainda " +
                $"não estão Pass (fail-closed): {summary}.");
        }

        // Sanitização (forma + guarda de segredo/PII) acontece dentro de CanaryScenarioResult.Create, chamado
        // pela store ao persistir — nunca duplicada aqui (EvidenceText é interno ao assembly Domain).
        var normalizedNotes = command.Notes ?? string.Empty;
        var fingerprint = DeterministicHash.Compute(
            [
                "archivebridge.canary.first-wave-approval.v1",
                command.Scope.Tenant.Value.ToString("N"),
                command.Scope.Project.Value.ToString("N"),
                command.PlanVersion.ToString(CultureInfo.InvariantCulture),
                actor,
                command.Correlation.Value.ToString("N"),
            ]);
        var locator = $"canary-first-wave-approval:v{command.PlanVersion.ToString(CultureInfo.InvariantCulture)}";
        var evidence = CanaryEvidenceReference.ApprovalDecision(fingerprint, locator);

        return await resultStore.RecordResultAsync(
            command.Scope,
            command.PlanVersion,
            CanaryScenarioCatalog.FirstWaveApprovalScenarioId,
            CanaryScenarioStatus.Pass,
            evidence,
            normalizedNotes,
            now,
            actor,
            role,
            command.Correlation,
            now,
            cancellationToken).ConfigureAwait(false);
    }
}
