using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// B8: identidade do archive. O construtor baseado só na mailbox marca a identidade como NÃO resolvida
/// (aliases não unificados); o construtor com <see cref="TargetArchiveId"/> explícito (manifesto
/// autorizado) marca como resolvida. Uma seleção com identidade não resolvida é sinalizada
/// (fail-closed na validação).
/// </summary>
public sealed class Slice2ArchiveIdentityTests
{
    [Fact]
    public void MailboxOnlyIsUnresolved() =>
        Assert.False(new ArchiveRef("u@contoso.com").IsIdentityResolved);

    [Fact]
    public void ExplicitIdentityIsResolved() =>
        Assert.True(new ArchiveRef("u@contoso.com", new TargetArchiveId("archive-123")).IsIdentityResolved);

    [Fact]
    public void RehydratePreservesResolution()
    {
        Assert.False(ArchiveRef.Rehydrate("u@contoso.com", new TargetArchiveId("u@contoso.com"), false).IsIdentityResolved);
        Assert.True(ArchiveRef.Rehydrate("u@contoso.com", new TargetArchiveId("u@contoso.com"), true).IsIdentityResolved);
    }

    [Fact]
    public void SelectionFlagsUnresolvedArchive()
    {
        var resolved = new WaveSelection([
            new WaveEntry("/a/x.pst", "x.pst", new ArchiveRef("u@contoso.com", new TargetArchiveId("u@contoso.com")), 1, 1),
        ]);
        Assert.False(resolved.HasUnresolvedArchive);

        var unresolved = new WaveSelection([
            new WaveEntry("/a/x.pst", "x.pst", new ArchiveRef("u@contoso.com", new TargetArchiveId("u@contoso.com")), 1, 1),
            new WaveEntry("/a/y.pst", "y.pst", new ArchiveRef("v@contoso.com"), 1, 1),
        ]);
        Assert.True(unresolved.HasUnresolvedArchive);
    }

    [Fact]
    public void ResolvedAliasesUnifyCapacityGrouping()
    {
        var canonical = new TargetArchiveId("shared-archive");
        var selection = new WaveSelection([
            new WaveEntry("/a/1.pst", "1.pst", new ArchiveRef("primary@contoso.com", canonical), 60, 1),
            new WaveEntry("/a/2.pst", "2.pst", new ArchiveRef("alias@contoso.com", canonical), 50, 1),
        ]);

        var byArchive = selection.BytesByArchive();
        Assert.Single(byArchive);
        Assert.Equal(110, byArchive[canonical]);
    }
}
