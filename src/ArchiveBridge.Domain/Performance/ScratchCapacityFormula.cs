namespace ArchiveBridge.Domain.Performance;

/// <summary>
/// Entradas da fórmula de scratch do runbook (§46): <c>sourceCopyBytes + expectedPartBytes +
/// repairBackupBytes + engineTemporaryOverhead + safetyMargin(20%)</c>. Um termo que ainda não existe na
/// implementação atual (ex.: <c>repairBackupBytes</c> — não há engine de repair/split aceita) é
/// representado explicitamente como zero, nunca omitido silenciosamente do cálculo.
/// </summary>
public readonly record struct ScratchCapacityInputs(
    long SourceCopyBytes,
    long ExpectedPartBytes,
    long RepairBackupBytes,
    long EngineTemporaryOverheadBytes);

/// <summary>Motivo pelo qual a fórmula recusou calcular um requisito de scratch (fail-closed).</summary>
public enum ScratchCapacityFormulaError
{
    /// <summary>Um dos termos de entrada é negativo — unidade/medição ambígua, nunca aceita silenciosamente.</summary>
    NegativeInput,

    /// <summary>A soma dos termos (ou a soma com a margem de segurança) excede <see cref="long.MaxValue"/>.</summary>
    Overflow,
}

/// <summary>
/// Materializa a fórmula de capacidade de scratch do runbook (§46) como aritmética inteira determinística,
/// fail-closed sobre overflow e valores negativos (AB-I7-003 §4). Nunca introduz uma nova engine de
/// repair/split — apenas soma os termos já documentados, cada um informado explicitamente pelo chamador.
/// </summary>
public static class ScratchCapacityFormula
{
    /// <summary>Margem de segurança do runbook: 20% da soma dos demais termos, arredondada PARA CIMA.</summary>
    public const int SafetyMarginPercent = 20;

    /// <summary>
    /// Tenta calcular o requisito total de scratch (bytes), incluindo a margem de segurança de 20%.
    /// Devolve <see langword="false"/> — sem lançar exceção — quando qualquer termo é negativo ou quando a
    /// soma (com margem) transborda <see cref="long"/>; nestes casos <paramref name="requiredScratchBytes"/>
    /// é sempre zero e <paramref name="error"/> identifica o motivo. A margem é arredondada PARA CIMA
    /// (nunca para baixo) para nunca subestimar o requisito por truncamento de divisão inteira.
    /// </summary>
    public static bool TryCompute(
        ScratchCapacityInputs inputs, out long requiredScratchBytes, out ScratchCapacityFormulaError? error)
    {
        requiredScratchBytes = 0;
        error = null;

        if (inputs.SourceCopyBytes < 0 || inputs.ExpectedPartBytes < 0
            || inputs.RepairBackupBytes < 0 || inputs.EngineTemporaryOverheadBytes < 0)
        {
            error = ScratchCapacityFormulaError.NegativeInput;
            return false;
        }

        try
        {
            checked
            {
                var baseBytes = inputs.SourceCopyBytes + inputs.ExpectedPartBytes
                    + inputs.RepairBackupBytes + inputs.EngineTemporaryOverheadBytes;

                // Divisão de teto (ceiling) em aritmética inteira: ceil(base * 20 / 100) == (base*20 + 99) / 100.
                // Nunca usa double (arredondamento de ponto flutuante não é aceitável para uma fronteira de
                // segurança fail-closed).
                var margin = ((baseBytes * SafetyMarginPercent) + 99) / 100;
                requiredScratchBytes = baseBytes + margin;
            }

            return true;
        }
        catch (OverflowException)
        {
            requiredScratchBytes = 0;
            error = ScratchCapacityFormulaError.Overflow;
            return false;
        }
    }
}
