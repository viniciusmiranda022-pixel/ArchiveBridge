namespace ArchiveBridge.Domain.Performance;

/// <summary>
/// Veredito de orçamento de capacidade de scratch. <see cref="Unknown"/> nunca é promovido a
/// <see cref="Enough"/> por default (AB-I7-003 §4) — é um estado terminal próprio, distinto de sucesso.
/// </summary>
public enum CapacityBudgetOutcome
{
    /// <summary>Capacidade disponível conhecida e suficiente para o requisito calculado.</summary>
    Enough,

    /// <summary>Capacidade disponível conhecida e insuficiente para o requisito calculado.</summary>
    Insufficient,

    /// <summary>
    /// Capacidade disponível desconhecida ou não confiável (nula ou negativa/ambígua). NUNCA equivalente a
    /// <see cref="Enough"/> — qualquer chamador que trate <see cref="Unknown"/> como "pode prosseguir"
    /// reintroduz exatamente o comportamento que este tipo existe para impedir.
    /// </summary>
    Unknown,
}

/// <summary>Resultado determinístico e auditável de UMA avaliação de orçamento de scratch.</summary>
/// <param name="RequiredScratchBytes">Requisito calculado por <see cref="ScratchCapacityFormula"/>.</param>
/// <param name="AvailableScratchBytes">Capacidade observada, ou <see langword="null"/> quando desconhecida.</param>
/// <param name="Outcome">Veredito fail-closed.</param>
/// <param name="Reason">Justificativa sanitizada (sem caminho/segredo), sempre presente.</param>
public sealed record ScratchCapacityBudgetAssessment(
    long RequiredScratchBytes, long? AvailableScratchBytes, CapacityBudgetOutcome Outcome, string Reason);

/// <summary>
/// Compara um requisito de scratch (já calculado por <see cref="ScratchCapacityFormula"/>) contra a
/// capacidade disponível observada, aplicando o invariante fail-closed central do AB-I7-003 §4:
/// ausência/ambiguidade de medição nunca vira aprovação implícita.
/// </summary>
public static class ScratchCapacityAssessor
{
    /// <summary>
    /// Avalia o orçamento. <paramref name="availableScratchBytes"/> nulo representa "não medido/desconhecido"
    /// — devolve sempre <see cref="CapacityBudgetOutcome.Unknown"/>, nunca <see cref="CapacityBudgetOutcome.Enough"/>.
    /// Um valor negativo é tratado como medição ambígua/não confiável (mesma unidade indefinida que o
    /// runbook trata como falha fechada) e também resulta em <see cref="CapacityBudgetOutcome.Unknown"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requiredScratchBytes"/> é negativo.</exception>
    public static ScratchCapacityBudgetAssessment Assess(long requiredScratchBytes, long? availableScratchBytes)
    {
        if (requiredScratchBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredScratchBytes), "O requisito de scratch não pode ser negativo.");
        }

        if (availableScratchBytes is null)
        {
            return new ScratchCapacityBudgetAssessment(
                requiredScratchBytes, null, CapacityBudgetOutcome.Unknown,
                "Capacidade disponível desconhecida (não medida) — nunca tratada como suficiente por default.");
        }

        if (availableScratchBytes.Value < 0)
        {
            return new ScratchCapacityBudgetAssessment(
                requiredScratchBytes, availableScratchBytes, CapacityBudgetOutcome.Unknown,
                "Valor de capacidade disponível negativo/ambíguo — tratado como desconhecido, nunca como suficiente.");
        }

        return availableScratchBytes.Value >= requiredScratchBytes
            ? new ScratchCapacityBudgetAssessment(
                requiredScratchBytes, availableScratchBytes, CapacityBudgetOutcome.Enough,
                "Capacidade disponível cobre o requisito calculado (incluindo margem de segurança).")
            : new ScratchCapacityBudgetAssessment(
                requiredScratchBytes, availableScratchBytes, CapacityBudgetOutcome.Insufficient,
                "Capacidade disponível é menor que o requisito calculado (incluindo margem de segurança).");
    }
}
