using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Comando de decisão sobre UMA exceção técnica de reconciliação (AB-I6-010): o caller fornece SOMENTE
/// identificadores opacos (<see cref="ItemKind"/>/<see cref="ItemKey"/>) e a decisão solicitada — a
/// exceção em si (resultado técnico, avaliação vigente) é sempre resolvida server-side (item 2).
/// <paramref name="AssessmentVersion"/> é a versão que o caller observou ao decidir (usada para detectar
/// staleness — item 8); <paramref name="ExpectedCurrentDecisionVersion"/> é a versão de decisão que o
/// caller acredita ser a vigente (0 = nenhuma decisão ainda esperada) — usada para detectar decisões
/// conflitantes concorrentes (item 10).
/// </summary>
public sealed record DisposeReconciliationExceptionCommand(
    TenantScope Scope,
    WaveId Wave,
    PurviewImportJobName PlannedJobName,
    int AssessmentVersion,
    ReconciliationExceptionItemKind ItemKind,
    string ItemKey,
    ReconciliationExceptionDecisionStatus RequestedStatus,
    ReconciliationExceptionReasonCode ReasonCode,
    int ExpectedCurrentDecisionVersion,
    string? Comment,
    string ActorId,
    string ActorRole,
    CorrelationId Correlation);

/// <summary>
/// RBAC server-side do workflow de disposition (item 5 do work order): resolve o catálogo concreto de
/// papéis do portal (<see cref="PortalRoles"/>) contra os invariantes PUROS de
/// <see cref="ReconciliationExceptionDispositionRules"/>. Um operador sem papel adequado nunca cria/altera
/// uma decisão; a mensagem nunca distingue "papel insuficiente" de "exceção inexistente/fora de escopo"
/// (mesmo padrão anti-enumeração de <see cref="ReconciliationExceptionNotFoundException"/>).
/// </summary>
internal static class ReconciliationExceptionDispositionAuthorization
{
    // Approver/Administrator: mesmo par que já decide projetos/ondas (Contracts.ControlPlane.PortalRoles) —
    // disposition de exceções é uma decisão de aprovação, nunca uma ação operacional de rotina.
    private static readonly HashSet<string> WriteRoles = new(StringComparer.Ordinal) { PortalRoles.Approver, PortalRoles.Administrator };

    /// <summary>Exige um papel de escrita conhecido — sem verificar ainda a transição específica.</summary>
    /// <exception cref="ReconciliationExceptionAuthorizationException">Papel desconhecido ou fora do conjunto autorizado.</exception>
    public static void EnsureCanWrite(string role)
    {
        if (string.IsNullOrWhiteSpace(role) || !PortalRoles.IsKnown(role) || !WriteRoles.Contains(role))
        {
            throw new ReconciliationExceptionAuthorizationException(
                "Papel não autorizado a criar/alterar disposition de exceções de reconciliação (fail-closed).");
        }
    }

    /// <summary>
    /// Exige, ADICIONALMENTE, o papel Administrator quando a transição concreta exige autorização elevada
    /// (item 12 — aceitar IncompleteEvidence como AcceptedException).
    /// </summary>
    /// <exception cref="ReconciliationExceptionAuthorizationException">A transição exige Administrator e o papel do ator não é Administrator.</exception>
    public static void EnsureElevatedIfRequired(
        string role, ReconciliationDisposition technicalDisposition, ReconciliationExceptionDecisionStatus requestedStatus)
    {
        if (ReconciliationExceptionDispositionRules.RequiresElevatedAuthorization(technicalDisposition, requestedStatus)
            && !string.Equals(role, PortalRoles.Administrator, StringComparison.Ordinal))
        {
            throw new ReconciliationExceptionAuthorizationException(
                "Aceitar uma exceção IncompleteEvidence como AcceptedException exige o papel Administrator (fail-closed).");
        }
    }

    /// <summary>Exige um ator identificado (nunca anônimo).</summary>
    /// <exception cref="ReconciliationExceptionAuthorizationException">O ator é vazio/whitespace.</exception>
    public static string RequireActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ReconciliationExceptionAuthorizationException("Decisão anônima não é permitida (ator obrigatório).");
        }

        return actorId.Trim();
    }
}

/// <summary>
/// Registra uma decisão humana/auditável sobre UMA exceção técnica de reconciliação (AB-I6-010). A
/// exceção-fonte (resultado técnico, avaliação vigente) é SEMPRE resolvida server-side a partir da
/// avaliação canônica mais recente (Passo 3) — nunca a partir de dados fornecidos pelo caller além dos
/// identificadores opacos. Nunca escreve em EXO/Graph/Purview/EV, nunca emite certificate, nunca fecha
/// wave/projeto (STOP-THE-LINE).
/// </summary>
public sealed class DisposeReconciliationExceptionUseCase(
    IReconciliationAssessmentStore assessments,
    IReconciliationExceptionDispositionStore dispositions,
    IClock clock)
{
    private const int MaxItemKeyLength = 320;

    private readonly IReconciliationAssessmentStore _assessments = assessments;
    private readonly IReconciliationExceptionDispositionStore _dispositions = dispositions;
    private readonly IClock _clock = clock;

    /// <exception cref="ReconciliationExceptionDispositionValidationException">Entrada estruturalmente inválida (status/motivo/comentário/ItemKey).</exception>
    /// <exception cref="ReconciliationExceptionAuthorizationException">Ator anônimo ou papel não autorizado para a transição.</exception>
    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda/plano inexistente/fora de escopo, ou nenhuma avaliação ainda computada (anti-IDOR).</exception>
    /// <exception cref="ReconciliationExceptionStaleAssessmentException">A avaliação referenciada não é mais a vigente.</exception>
    /// <exception cref="ReconciliationExceptionNotFoundException">O item referenciado não existe na avaliação vigente (anti-IDOR).</exception>
    /// <exception cref="ReconciliationExceptionNotDispositionableException">O item não é uma exceção passível de disposition.</exception>
    /// <exception cref="ConcurrencyException">Uma decisão conflitante concorrente já é a vigente.</exception>
    public async Task<ReconciliationExceptionDecision> ExecuteAsync(
        DisposeReconciliationExceptionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1-3: validação/autorização PURAMENTE de entrada, sempre ANTES de qualquer acesso a dado
        // pertencente a um escopo — um ator sem papel de escrita recebe exatamente a mesma recusa
        // independentemente de a exceção referenciada existir ou pertencer a outro tenant/projeto/onda
        // (item 6: nunca revela existência cross-scope).
        ReconciliationExceptionDispositionRules.EnsureStatusIsExplicitlyDecidable(command.RequestedStatus);
        ReconciliationExceptionDispositionAuthorization.EnsureCanWrite(command.ActorRole);
        var actor = ReconciliationExceptionDispositionAuthorization.RequireActor(command.ActorId);
        var itemKey = RequireItemKey(command.ItemKey);

        var latest = await _assessments.GetLatestAsync(command.Scope, command.Wave, command.PlannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException(
                "Onda/plano inexistente ou fora do escopo autorizado, ou nenhuma avaliação de reconciliação ainda computada (fail-closed).");

        if (latest.AssessmentVersion != command.AssessmentVersion)
        {
            throw new ReconciliationExceptionStaleAssessmentException(
                "A avaliação de reconciliação referenciada não é mais a vigente (foi superseded) — releia o estado " +
                "atual antes de decidir (fail-closed).");
        }

        var technicalDisposition = command.ItemKind switch
        {
            ReconciliationExceptionItemKind.Pst => await ResolvePstDispositionAsync(command, latest.AssessmentVersion, itemKey, cancellationToken)
                .ConfigureAwait(false),
            ReconciliationExceptionItemKind.Archive => await ResolveArchiveDispositionAsync(command, latest.AssessmentVersion, itemKey, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ReconciliationExceptionDispositionValidationException("ItemKind desconhecido (fail-closed)."),
        };

        ReconciliationExceptionDispositionRules.EnsureDispositionable(technicalDisposition);
        ReconciliationExceptionDispositionAuthorization.EnsureElevatedIfRequired(command.ActorRole, technicalDisposition, command.RequestedStatus);
        ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
            technicalDisposition, command.RequestedStatus, command.ReasonCode, ReconciliationExceptionReasonCodeCatalog.CurrentVersion);

        return await _dispositions.SaveDecisionAsync(
            command.Scope,
            command.Wave,
            command.PlannedJobName,
            latest.AssessmentVersion,
            latest.SourceFingerprint,
            command.ItemKind,
            itemKey,
            technicalDisposition,
            command.ExpectedCurrentDecisionVersion,
            command.RequestedStatus,
            command.ReasonCode,
            ReconciliationExceptionReasonCodeCatalog.CurrentVersion,
            command.Comment,
            actor,
            command.ActorRole,
            command.Correlation,
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    private static string RequireItemKey(string? itemKey)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
        {
            throw new ReconciliationExceptionDispositionValidationException("ItemKey é obrigatório (fail-closed).");
        }

        var trimmed = itemKey.Trim();
        if (trimmed.Length > MaxItemKeyLength)
        {
            throw new ReconciliationExceptionDispositionValidationException(
                $"ItemKey excede {MaxItemKeyLength} caracteres (fail-closed).");
        }

        return trimmed;
    }

    private async Task<ReconciliationDisposition> ResolvePstDispositionAsync(
        DisposeReconciliationExceptionCommand command, int assessmentVersion, string itemKey, CancellationToken cancellationToken)
    {
        var items = await _assessments.GetPstItemsAsync(command.Scope, command.Wave, command.PlannedJobName, assessmentVersion, cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in items)
        {
            if (string.Equals(item.RemoteName.Value, itemKey, StringComparison.Ordinal))
            {
                return item.Disposition;
            }
        }

        throw new ReconciliationExceptionNotFoundException(
            "Item de PST referenciado não existe na avaliação vigente ou está fora do escopo autorizado (fail-closed).");
    }

    private async Task<ReconciliationDisposition> ResolveArchiveDispositionAsync(
        DisposeReconciliationExceptionCommand command, int assessmentVersion, string itemKey, CancellationToken cancellationToken)
    {
        var items = await _assessments.GetArchiveItemsAsync(command.Scope, command.Wave, command.PlannedJobName, assessmentVersion, cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in items)
        {
            if (string.Equals(item.Archive.Value, itemKey, StringComparison.Ordinal))
            {
                return item.Disposition;
            }
        }

        throw new ReconciliationExceptionNotFoundException(
            "Item de archive referenciado não existe na avaliação vigente ou está fora do escopo autorizado (fail-closed).");
    }
}
