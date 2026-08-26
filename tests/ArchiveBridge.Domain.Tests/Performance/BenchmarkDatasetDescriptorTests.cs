using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>AB-I7-003 §7/§9 — rótulo do dataset é sempre sintético, nunca um caminho/endereço real.</summary>
public sealed class BenchmarkDatasetDescriptorTests
{
    [Fact]
    public void ASyntheticLabelIsAccepted()
    {
        var descriptor = new BenchmarkDatasetDescriptor("synthetic-small-4KiB", sizeBytes: 4096, itemCount: 1, seed: 42);

        Assert.Equal("synthetic-small-4KiB", descriptor.Name);
        Assert.Equal(4096, descriptor.SizeBytes);
        Assert.Equal(1, descriptor.ItemCount);
        Assert.Equal(42, descriptor.Seed);
    }

    [Theory]
    [InlineData("C:/tmp/real-mailbox.pst")]
    [InlineData("C:\\tmp\\real-mailbox.pst")]
    [InlineData("user@example.com")]
    public void ALabelThatLooksLikeAPathOrAddressIsRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkDatasetDescriptor(name, sizeBytes: 0, itemCount: 0, seed: 0));
    }

    [Fact]
    public void NegativeSizeBytesThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkDatasetDescriptor("synthetic", sizeBytes: -1, itemCount: 0, seed: 0));
    }

    [Fact]
    public void NegativeItemCountThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkDatasetDescriptor("synthetic", sizeBytes: 0, itemCount: -1, seed: 0));
    }
}
