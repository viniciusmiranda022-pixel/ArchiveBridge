using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I5-013 item 1 — <see cref="WaveEntryId"/> é uma identidade OPACA e DETERMINÍSTICA, nunca um
/// índice/ordinal: mesma onda + mesmo conteúdo de entrada sempre produz o mesmo ID; qualquer campo
/// imutável diferente produz um ID diferente; e <see cref="WaveSelection.ResolveEntry"/> nunca usa posição
/// para localizar a entrada correspondente.
/// </summary>
public sealed class WaveEntryIdDomainTests
{
    private static WaveEntry NewEntry(string pstName = "mailbox-a.pst", string mailbox = "mailbox-a@contoso.com", long sizeBytes = 4096, long itemCount = 10) =>
        new($"C:\\pst\\{pstName}", pstName, new ArchiveRef(mailbox), sizeBytes, itemCount);

    [Fact]
    public void DeriveIsDeterministicForTheSameWaveAndTheSameEntryContent()
    {
        var wave = WaveId.New();
        var entry = NewEntry();

        var first = WaveEntryId.Derive(wave, entry);
        var second = WaveEntryId.Derive(wave, new WaveEntry(entry.FilePath, entry.PstName, entry.Archive, entry.SizeBytes, entry.ItemCount));

        Assert.Equal(first, second);
    }

    [Fact]
    public void DeriveProducesADifferentIdForADifferentWaveEvenWithIdenticalEntryContent()
    {
        var entry = NewEntry();

        var first = WaveEntryId.Derive(WaveId.New(), entry);
        var second = WaveEntryId.Derive(WaveId.New(), entry);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("other.pst", "mailbox-a@contoso.com", 4096L, 10L)]
    [InlineData("mailbox-a.pst", "other@contoso.com", 4096L, 10L)]
    [InlineData("mailbox-a.pst", "mailbox-a@contoso.com", 8192L, 10L)]
    [InlineData("mailbox-a.pst", "mailbox-a@contoso.com", 4096L, 99L)]
    public void DeriveProducesADifferentIdWhenAnyImmutableFieldOfTheEntryDiffers(
        string pstName, string mailbox, long sizeBytes, long itemCount)
    {
        var wave = WaveId.New();
        var baseline = WaveEntryId.Derive(wave, NewEntry());
        var varied = WaveEntryId.Derive(wave, NewEntry(pstName, mailbox, sizeBytes, itemCount));

        Assert.NotEqual(baseline, varied);
    }

    [Fact]
    public void ResolveEntryFindsTheMatchingEntryRegardlessOfItsPositionInTheSelection()
    {
        var wave = WaveId.New();
        var first = NewEntry("first.pst", "first@contoso.com");
        var second = NewEntry("second.pst", "second@contoso.com");
        var selection = new WaveSelection([first, second]);
        var secondId = WaveEntryId.Derive(wave, second);

        var resolved = selection.ResolveEntry(wave, secondId);

        Assert.Equal(second, resolved);
    }

    [Fact]
    public void ResolveEntryReturnsNullForAnIdThatDoesNotBelongToTheSelection()
    {
        var wave = WaveId.New();
        var selection = new WaveSelection([NewEntry()]);
        var foreignId = WaveEntryId.Derive(wave, NewEntry("foreign.pst", "foreign@contoso.com"));

        Assert.Null(selection.ResolveEntry(wave, foreignId));
    }

    [Fact]
    public void ResolveEntryReturnsNullWhenTheIdWasDerivedForADifferentWave()
    {
        // Mesmo conteúdo de entrada, mas o ID opaco foi calculado para OUTRA onda — não deve casar,
        // reforçando que a identidade nunca é global, só faz sentido dentro da onda que a gerou.
        var entry = NewEntry();
        var selection = new WaveSelection([entry]);
        var idForAnotherWave = WaveEntryId.Derive(WaveId.New(), entry);

        Assert.Null(selection.ResolveEntry(WaveId.New(), idForAnotherWave));
    }
}
