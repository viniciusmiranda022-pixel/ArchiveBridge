using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>AB-I7-003 §9 — sanitização/validade estrutural de uma medição.</summary>
public sealed class BenchmarkMeasurementTests
{
    [Fact]
    public void NegativeIterationIndexThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BenchmarkMeasurement(-1, 10, null, null, null, null, BenchmarkIterationOutcome.Success));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void InvalidWallClockThrows(double wallClockMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BenchmarkMeasurement(0, wallClockMs, null, null, null, null, BenchmarkIterationOutcome.Success));
    }

    [Fact]
    public void NegativeOptionalFieldsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkMeasurement(0, 1, -1, null, null, null, BenchmarkIterationOutcome.Success));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkMeasurement(0, 1, null, -1, null, null, BenchmarkIterationOutcome.Success));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkMeasurement(0, 1, null, null, -1, null, BenchmarkIterationOutcome.Success));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkMeasurement(0, 1, null, null, null, -1, BenchmarkIterationOutcome.Success));
    }

    [Fact]
    public void BytesPerSecondIsComputedFromBytesProcessedAndWallClock()
    {
        var measurement = new BenchmarkMeasurement(0, wallClockMs: 500, null, null, bytesProcessed: 1_000_000, null, BenchmarkIterationOutcome.Success);

        Assert.NotNull(measurement.BytesPerSecond);
        Assert.Equal(2_000_000, measurement.BytesPerSecond!.Value, precision: 3);
    }

    [Fact]
    public void BytesPerSecondIsNullWithoutBytesProcessed()
    {
        var measurement = new BenchmarkMeasurement(0, wallClockMs: 500, null, null, bytesProcessed: null, null, BenchmarkIterationOutcome.Success);

        Assert.Null(measurement.BytesPerSecond);
    }

    [Fact]
    public void BytesPerSecondIsNullWhenWallClockIsZero()
    {
        var measurement = new BenchmarkMeasurement(0, wallClockMs: 0, null, null, bytesProcessed: 1000, null, BenchmarkIterationOutcome.Success);

        Assert.Null(measurement.BytesPerSecond);
    }

    [Fact]
    public void ItemsPerSecondIsComputedFromItemsProcessedAndWallClock()
    {
        var measurement = new BenchmarkMeasurement(0, wallClockMs: 1000, null, null, null, itemsProcessed: 50, BenchmarkIterationOutcome.Success);

        Assert.NotNull(measurement.ItemsPerSecond);
        Assert.Equal(50, measurement.ItemsPerSecond!.Value, precision: 3);
    }
}
