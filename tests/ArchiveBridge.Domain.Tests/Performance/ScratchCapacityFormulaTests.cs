using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>AB-I7-003 §4/§9 — fórmula de scratch do runbook, margem de 20% arredondada para cima, overflow e negativos fail-closed.</summary>
public sealed class ScratchCapacityFormulaTests
{
    [Fact]
    public void AllZeroInputsProduceZeroRequirement()
    {
        var inputs = new ScratchCapacityInputs(0, 0, 0, 0);

        var ok = ScratchCapacityFormula.TryCompute(inputs, out var required, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(0, required);
    }

    [Theory]
    [InlineData(100, 120)] // 100 * 20% = 20 exato ⇒ 120
    [InlineData(101, 122)] // 101 * 20% = 20.2 ⇒ teto 21 ⇒ 122
    [InlineData(1, 2)] // 1 * 20% = 0.2 ⇒ teto 1 ⇒ 2
    [InlineData(5, 6)] // 5 * 20% = 1.0 exato ⇒ 6
    public void SafetyMarginIsAlwaysRoundedUpNeverDown(long sourceCopyBytes, long expectedRequired)
    {
        var inputs = new ScratchCapacityInputs(sourceCopyBytes, 0, 0, 0);

        var ok = ScratchCapacityFormula.TryCompute(inputs, out var required, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(expectedRequired, required);
    }

    [Fact]
    public void AllFourTermsAreSummedBeforeApplyingTheMargin()
    {
        // sourceCopyBytes + expectedPartBytes + repairBackupBytes + engineTemporaryOverhead = 1000 ⇒ margem 200 ⇒ 1200.
        var inputs = new ScratchCapacityInputs(400, 300, 200, 100);

        var ok = ScratchCapacityFormula.TryCompute(inputs, out var required, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(1200, required);
    }

    [Fact]
    public void ATermNotApplicableToTheCurrentImplementationIsRepresentedAsExplicitZeroNeverOmitted()
    {
        // repairBackupBytes=0 (nenhuma engine de repair/split aceita ainda) — participa explicitamente da
        // soma como zero, o resultado é idêntico a computar sem o termo.
        var withZeroRepair = new ScratchCapacityInputs(1000, 0, 0, 0);
        var withoutRepairTerm = new ScratchCapacityInputs(1000, 0, 0, 0);

        ScratchCapacityFormula.TryCompute(withZeroRepair, out var required1, out _);
        ScratchCapacityFormula.TryCompute(withoutRepairTerm, out var required2, out _);

        Assert.Equal(required1, required2);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void AnyNegativeTermFailsClosedWithoutThrowing(long sourceCopyBytes, long expectedPartBytes, long repairBackupBytes, long overhead)
    {
        var inputs = new ScratchCapacityInputs(sourceCopyBytes, expectedPartBytes, repairBackupBytes, overhead);

        var ok = ScratchCapacityFormula.TryCompute(inputs, out var required, out var error);

        Assert.False(ok);
        Assert.Equal(ScratchCapacityFormulaError.NegativeInput, error);
        Assert.Equal(0, required);
    }

    [Fact]
    public void SumOverflowingLongFailsClosedWithoutThrowing()
    {
        var inputs = new ScratchCapacityInputs(long.MaxValue, 1, 0, 0);

        var ok = ScratchCapacityFormula.TryCompute(inputs, out var required, out var error);

        Assert.False(ok);
        Assert.Equal(ScratchCapacityFormulaError.Overflow, error);
        Assert.Equal(0, required);
    }

    [Fact]
    public void MarginMultiplicationOverflowingLongFailsClosedWithoutThrowing()
    {
        // baseBytes cabe em long, mas baseBytes * 20 (antes da divisão por 100) não cabe.
        var inputs = new ScratchCapacityInputs(long.MaxValue / 10, 0, 0, 0);

        var ok = ScratchCapacityFormula.TryCompute(inputs, out var required, out var error);

        Assert.False(ok);
        Assert.Equal(ScratchCapacityFormulaError.Overflow, error);
        Assert.Equal(0, required);
    }
}
