using ArchiveBridge.Domain.Planning;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Motivo estruturado de bloqueio do precheck/capacity gate (runbook §25.4, work order AB-I5-001 itens
/// 6-10) — fail-closed, nunca uma mensagem livre interpolando dado do ambiente.
/// </summary>
public enum PurviewPrecheckBlockReason
{
    /// <summary>Sem bloqueio — o caminho pode prosseguir.</summary>
    None,

    /// <summary>Nenhuma capability evidence registrada para a rota neste escopo.</summary>
    CapabilityEvidenceMissing,

    /// <summary>Capability <see cref="CapabilityStatus.Unknown"/> — bloqueia fail-closed (item 2).</summary>
    CapabilityUnknown,

    /// <summary>Capability explicitamente <see cref="CapabilityStatus.Unsupported"/>.</summary>
    CapabilityUnsupported,

    /// <summary>Capability é Preview/Contractual — nunca promovida implicitamente a GA (item 2).</summary>
    CapabilityNotGeneralAvailability,

    /// <summary>Capability evidence mais antiga que a janela de frescor aceita (item 13).</summary>
    CapabilityEvidenceStale,

    /// <summary>Nenhum precheck de mailbox registrado para o archive.</summary>
    MailboxPrecheckMissing,

    /// <summary><see cref="MailboxArchiveStatus"/> diferente de <see cref="MailboxArchiveStatus.Active"/> (item 7).</summary>
    ArchiveInactive,

    /// <summary>Alguma parte planejada excede <see cref="PurviewPolicyLimits.HardPartBytes"/> (item 7).</summary>
    PartExceedsPolicy,

    /// <summary>Contagem de linhas do CSV excede <see cref="PurviewPolicyLimits.MaxCsvDataRows"/> (item 7).</summary>
    CsvRowLimitExceeded,

    /// <summary>
    /// Volume planejado para o archive excede <see cref="PurviewPolicyLimits.MainArchiveImportLimitBytes"/> —
    /// <see cref="MailboxPrecheckSnapshot.AutoExpandingArchiveEnabled"/> NUNCA eleva este limite (item 8).
    /// Código reutilizado de <see cref="CapacityRule.AssessmentRequiredCode"/> (mesmo significado do gate de
    /// capacidade por onda, Slice 2).
    /// </summary>
    MainArchiveImportLimitExceeded,

    /// <summary>Capacidade disponível não observada pelo precheck — o gate de margem não pode rodar (fail-closed).</summary>
    CapacityNotObserved,

    /// <summary>Volume planejado excede a capacidade disponível observada menos a margem de segurança (item 10).</summary>
    CapacityMarginExceeded,
}

/// <summary>Resultado do precheck/capacity gate: permitido, ou bloqueado com motivo e código estruturados.</summary>
public sealed record PurviewPrecheckGateResult(bool Allowed, PurviewPrecheckBlockReason Reason, string ReasonCode)
{
    private const string WithinLimitCode = "WITHIN_LIMIT";

    /// <summary>Resultado permitido (nenhum bloqueio).</summary>
    public static PurviewPrecheckGateResult Allow() => new(true, PurviewPrecheckBlockReason.None, WithinLimitCode);

    /// <summary>Resultado bloqueado com motivo e código estruturados.</summary>
    public static PurviewPrecheckGateResult Block(PurviewPrecheckBlockReason reason, string reasonCode) =>
        new(false, reason, reasonCode);
}

/// <summary>
/// Gate DETERMINÍSTICO de precheck/capacidade para o caminho de archive import via Purview Network Upload
/// (runbook §25.4, work order AB-I5-001 itens 6-10). Puro: sem I/O, sem clock, sem chamada a adapter —
/// opera inteiramente sobre bytes/estruturas ESTRUTURADAS já resolvidas pelo chamador (nunca parsing
/// locale-dependent de string formatada, item 6). As checagens são avaliadas em ordem fixa e
/// curto-circuitam no primeiro bloqueio, mesmo desenho de <c>EvDeltaStrategySelectionPolicy</c>. A validação
/// de <c>TargetRootFolder != "/"</c> (item 9) não aparece aqui: é estruturalmente garantida por
/// <see cref="ArchiveBridge.Domain.Waves.TargetRootFolder"/>, cujo construtor já rejeita <c>"/"</c> — uma
/// onda aprovada nunca alcança este gate com uma pasta raiz inválida.
/// </summary>
public static class PurviewPrecheckGate
{
    /// <summary>Avalia o gate completo para UM archive de destino.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="csvRowCount"/> ou algum tamanho planejado é negativo.</exception>
    public static PurviewPrecheckGateResult EvaluateArchiveImport(
        PurviewPolicyLimits limits,
        CapabilityUsabilityOutcome capabilityOutcome,
        MailboxPrecheckSnapshot precheck,
        int csvRowCount,
        long plannedArchiveImportBytes,
        IReadOnlyList<long> plannedPartSizesBytes)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(precheck);
        ArgumentNullException.ThrowIfNull(plannedPartSizesBytes);

        if (csvRowCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(csvRowCount), csvRowCount, "csvRowCount não pode ser negativo.");
        }

        if (plannedArchiveImportBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plannedArchiveImportBytes), plannedArchiveImportBytes, "plannedArchiveImportBytes não pode ser negativo.");
        }

        foreach (var partSize in plannedPartSizesBytes)
        {
            if (partSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(plannedPartSizesBytes), partSize, "Nenhum tamanho de parte pode ser negativo.");
            }
        }

        var capabilityBlock = EvaluateCapabilityOnly(capabilityOutcome);
        if (capabilityBlock is not null)
        {
            return capabilityBlock;
        }

        if (precheck.ArchiveStatus != MailboxArchiveStatus.Active)
        {
            return PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.ArchiveInactive, "ARCHIVE_NOT_ACTIVE");
        }

        if (plannedPartSizesBytes.Any(size => size > limits.HardPartBytes))
        {
            return PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.PartExceedsPolicy, "PART_EXCEEDS_POLICY");
        }

        if (csvRowCount > limits.MaxCsvDataRows)
        {
            return PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CsvRowLimitExceeded, "CSV_ROW_LIMIT_EXCEEDED");
        }

        if (plannedArchiveImportBytes > limits.MainArchiveImportLimitBytes)
        {
            // AutoExpandingArchiveEnabled nunca é consultado aqui — o limite principal do adapter é fixo
            // independentemente do estado de auto-expansion da mailbox (item 8).
            return PurviewPrecheckGateResult.Block(
                PurviewPrecheckBlockReason.MainArchiveImportLimitExceeded, CapacityRule.AssessmentRequiredCode);
        }

        if (precheck.ObservedAvailableBytes is not { } availableBytes)
        {
            return PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapacityNotObserved, "CAPACITY_NOT_OBSERVED");
        }

        var marginBytes = availableBytes - limits.SafetyMarginBytes;
        if (plannedArchiveImportBytes > marginBytes)
        {
            return PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapacityMarginExceeded, "CAPACITY_MARGIN_EXCEEDED");
        }

        return PurviewPrecheckGateResult.Allow();
    }

    /// <summary>
    /// Avalia SOMENTE a checagem de capability, sem depender de um precheck de mailbox — útil quando o
    /// chamador precisa saber se TODOS os archives de uma onda estão bloqueados pela mesma rota/capability
    /// antes mesmo de ter um precheck por archive (ex.: <c>EvaluatePurviewPrecheckUseCase</c>). Devolve
    /// <see langword="null"/> quando a capability está <see cref="CapabilityUsabilityOutcome.Usable"/>
    /// (nenhum bloqueio nesta etapa).
    /// </summary>
    public static PurviewPrecheckGateResult? EvaluateCapabilityOnly(CapabilityUsabilityOutcome outcome) => outcome switch
    {
        CapabilityUsabilityOutcome.NoEvidence =>
            PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapabilityEvidenceMissing, "CAPABILITY_EVIDENCE_MISSING"),
        CapabilityUsabilityOutcome.Unknown =>
            PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapabilityUnknown, "CAPABILITY_UNKNOWN"),
        CapabilityUsabilityOutcome.Unsupported =>
            PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapabilityUnsupported, "CAPABILITY_UNSUPPORTED"),
        CapabilityUsabilityOutcome.NotGeneralAvailability =>
            PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapabilityNotGeneralAvailability, "CAPABILITY_NOT_GENERAL_AVAILABILITY"),
        CapabilityUsabilityOutcome.Stale =>
            PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapabilityEvidenceStale, "CAPABILITY_EVIDENCE_STALE"),
        CapabilityUsabilityOutcome.Usable => null,
        // Fail-closed default: qualquer valor futuro não mapeado explicitamente bloqueia, nunca passa.
        _ => PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.CapabilityUnknown, "CAPABILITY_UNKNOWN"),
    };
}
