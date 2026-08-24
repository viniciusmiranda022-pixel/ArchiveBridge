namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Desfecho de usabilidade de uma capability para um caminho que EXIGE
/// <see cref="CapabilityStatus.GeneralAvailability"/> (work order AB-I5-001 itens 2/13) — fail-closed em
/// toda ambiguidade, mesmo desenho de <c>EvDeltaStrategySelectionOutcome</c>.
/// </summary>
public enum CapabilityUsabilityOutcome
{
    /// <summary>Nenhuma evidência registrada para a rota neste escopo — nunca tratado como suportado por omissão.</summary>
    NoEvidence,

    /// <summary>A evidência mais recente é <see cref="CapabilityStatus.Unknown"/>.</summary>
    Unknown,

    /// <summary>A evidência mais recente é <see cref="CapabilityStatus.Unsupported"/>.</summary>
    Unsupported,

    /// <summary>
    /// A evidência mais recente é <see cref="CapabilityStatus.Preview"/> ou <see cref="CapabilityStatus.Contractual"/> —
    /// nunca promovida implicitamente a GA (work order item 2).
    /// </summary>
    NotGeneralAvailability,

    /// <summary>A evidência mais recente é mais antiga que a janela de frescor aceita — bloqueio explícito até nova evidência.</summary>
    Stale,

    /// <summary>Evidência <see cref="CapabilityStatus.GeneralAvailability"/> e dentro da janela de frescor.</summary>
    Usable,
}

/// <summary>
/// Política DETERMINÍSTICA de usabilidade de capability (work order AB-I5-001 itens 2/13): nunca infere —
/// a evidência mais recente decide sozinha, e um downgrade/contradição posterior (uma evidência mais nova
/// com status inferior) sempre prevalece sobre a mais antiga, pois a política só olha a MAIS RECENTE por
/// <see cref="CapabilityEvidence.RecordedAtUtc"/> (nunca a "melhor" historicamente).
/// </summary>
public static class CapabilityEvidencePolicy
{
    /// <summary>
    /// Janela de frescor default: política PRÓPRIA do produto (não documentada pela Microsoft) — 180 dias,
    /// conservadora e configurável; força redescoberta periódica em vez de confiar indefinidamente numa
    /// evidência antiga.
    /// </summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(180);

    /// <summary>Avalia se a evidência mais recente autoriza um caminho que exige GA.</summary>
    public static CapabilityUsabilityOutcome EnsureGeneralAvailability(
        CapabilityEvidence? latest, DateTimeOffset now, TimeSpan maxAge)
    {
        if (latest is null)
        {
            return CapabilityUsabilityOutcome.NoEvidence;
        }

        if (latest.Status == CapabilityStatus.Unknown)
        {
            return CapabilityUsabilityOutcome.Unknown;
        }

        if (latest.Status == CapabilityStatus.Unsupported)
        {
            return CapabilityUsabilityOutcome.Unsupported;
        }

        // Staleness usa RecordedAtUtc (quando a rota foi CONFIRMADA pela última vez), nunca ObservedAtUtc
        // (a data do fato documentado, que não muda a cada redescoberta) — senão a janela nunca "renovaria".
        if (now - latest.RecordedAtUtc > maxAge)
        {
            return CapabilityUsabilityOutcome.Stale;
        }

        if (latest.Status != CapabilityStatus.GeneralAvailability)
        {
            // Preview ou Contractual — nunca tratado como GA (work order item 2).
            return CapabilityUsabilityOutcome.NotGeneralAvailability;
        }

        return CapabilityUsabilityOutcome.Usable;
    }
}
