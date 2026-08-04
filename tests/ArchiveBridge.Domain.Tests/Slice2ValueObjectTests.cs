using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Tests;

public sealed class Slice2ValueObjectTests
{
    [Fact]
    public void TargetRootFolderForWaveUsesCanonicalFormat() =>
        Assert.Equal("/ImportedPst_ProjA_Wave1", TargetRootFolder.ForWave("ProjA", "Wave1").Value);

    [Theory]
    [InlineData("/ImportedPst_A_B")]
    [InlineData("/a/b/c")]
    [InlineData("/single")]
    public void TargetRootFolderAcceptsSafePaths(string value) =>
        Assert.Equal(value, new TargetRootFolder(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/no/leading/slash")]
    [InlineData("/")]
    [InlineData("/has/../traversal")]
    [InlineData("/has\\backslash")]
    [InlineData("/has space")]
    [InlineData("/has:colon")]
    [InlineData("/has*star")]
    public void TargetRootFolderRejectsUnsafePaths(string value) =>
        Assert.Throws<ArgumentException>(() => new TargetRootFolder(value));

    [Fact]
    public void TargetRootFolderCollapsesSlashesAndStripsTrailing()
    {
        Assert.Equal("/a/b", new TargetRootFolder("/a//b/").Value);
        Assert.Equal("/a/b", new TargetRootFolder("///a///b///").Value);
    }

    [Fact]
    public void TargetRootFolderCanonicalizationIsDeterministic() =>
        Assert.Equal(new TargetRootFolder("/a//b/").Value, new TargetRootFolder("/a/b").Value);

    [Fact]
    public void TargetRootFolderRejectsTooLong() =>
        Assert.Throws<ArgumentException>(() => new TargetRootFolder("/" + new string('a', 401)));

    [Fact]
    public void WaveSelectionRejectsDuplicatePstNames()
    {
        var entries = new List<WaveEntry>
        {
            new("/a/x.pst", "x.pst", new ArchiveRef("u@contoso.com"), 1, 1),
            new("/b/x.pst", "x.pst", new ArchiveRef("v@contoso.com"), 1, 1),
        };
        Assert.Throws<ArgumentException>(() => new WaveSelection(entries));
    }

    [Fact]
    public void WaveSelectionGroupsBytesByArchive()
    {
        var entries = new List<WaveEntry>
        {
            new("/a/1.pst", "1.pst", new ArchiveRef("shared@contoso.com"), 30, 1),
            new("/a/2.pst", "2.pst", new ArchiveRef("shared@contoso.com"), 70, 1),
            new("/a/3.pst", "3.pst", new ArchiveRef("other@contoso.com"), 5, 1),
        };
        var selection = new WaveSelection(entries);
        var byArchive = selection.BytesByArchive();

        Assert.Equal(100, byArchive[new TargetArchiveId("shared@contoso.com")]);
        Assert.Equal(5, byArchive[new TargetArchiveId("other@contoso.com")]);
        Assert.Equal(105, selection.TotalBytes);
    }

    [Fact]
    public void WaveSelectionHashIsDeterministicRegardlessOfInputOrder()
    {
        var a = new WaveSelection(new List<WaveEntry>
        {
            new("/a/1.pst", "1.pst", new ArchiveRef("u@contoso.com"), 1, 1),
            new("/a/2.pst", "2.pst", new ArchiveRef("v@contoso.com"), 2, 2),
        });
        var b = new WaveSelection(new List<WaveEntry>
        {
            new("/a/2.pst", "2.pst", new ArchiveRef("v@contoso.com"), 2, 2),
            new("/a/1.pst", "1.pst", new ArchiveRef("u@contoso.com"), 1, 1),
        });
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    [Theory]
    [InlineData("/a/../x.pst", "x.pst")]
    [InlineData("/a/x.pst", "x.txt")]
    [InlineData("/a/x.pst", "sub/x.pst")]
    public void WaveEntryRejectsUnsafePathsOrNames(string filePath, string pstName) =>
        Assert.Throws<ArgumentException>(() => new WaveEntry(filePath, pstName, new ArchiveRef("u@contoso.com"), 1, 1));

    [Fact]
    public void WaveEntryRejectsNegativeSizes() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaveEntry("/a/x.pst", "x.pst", new ArchiveRef("u@contoso.com"), -1, 0));

    [Fact]
    public void DeterministicHashOrderMatters()
    {
        var one = DeterministicHash.Compute(["a", "b"]);
        var two = DeterministicHash.Compute(["b", "a"]);
        Assert.NotEqual(one, two);
        Assert.Equal(one, DeterministicHash.Compute(["a", "b"]));
    }

    [Fact]
    public void DeterministicHashSeparatorPreventsCollision()
    {
        // "a" + "bc" não deve colidir com "ab" + "c" graças ao separador de unidade.
        Assert.NotEqual(DeterministicHash.Compute(["a", "bc"]), DeterministicHash.Compute(["ab", "c"]));
    }
}
