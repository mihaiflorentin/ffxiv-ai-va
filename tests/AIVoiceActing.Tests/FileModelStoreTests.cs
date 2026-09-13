namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using Xunit;

public sealed class FileModelStoreTests
{
    [Fact]
    public void Missing_EmptyWhenAllAssetsPresentWithinTolerance()
    {
        var dir = NewDir();
        foreach (var asset in SmallCatalog())
        {
            File.WriteAllBytes(Path.Combine(dir, asset.FileName), new byte[asset.SizeBytes!.Value]);
        }

        var store = new FileModelStore(dir, SmallCatalog());
        Assert.Empty(store.Missing());
        Assert.True(store.IsDownloaded("a.bin"));
    }

    [Fact]
    public void Missing_ListsAbsentAndCorruptAssets()
    {
        var dir = NewDir();
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[100]);       // ok
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[50]);        // >5% off → corrupt

        var store = new FileModelStore(dir, SmallCatalog());
        var missing = store.Missing().Select(a => a.FileName).ToArray();
        Assert.Equal(["b.bin", "c.bin"], missing);
        Assert.False(store.IsDownloaded("b.bin"));
        Assert.False(store.IsDownloaded("c.bin"));
    }

    [Fact]
    public void Missing_ExcludesOptionalAssets()
    {
        var dir = NewDir();
        var catalog = new List<ModelAsset>
        {
            new("opt", "opt.bin", 10, Optional: true),
        };
        var store = new FileModelStore(dir, catalog);
        Assert.Empty(store.Missing());
    }

    [Fact]
    public void Remove_DeletesFileAndStalePartTwin()
    {
        var dir = NewDir();
        var catalog = new List<ModelAsset> { new("a", "a.bin", 4) };
        var store = new FileModelStore(dir, catalog);
        File.WriteAllBytes(store.PathFor("a.bin"), [1, 2, 3, 4]);
        File.WriteAllBytes(store.PathFor("a.bin.part"), [9]);

        Assert.True(store.Remove("a.bin"));
        Assert.False(store.IsDownloaded("a.bin"));
        Assert.False(File.Exists(store.PathFor("a.bin")));
        Assert.False(File.Exists(store.PathFor("a.bin.part")));
    }

    [Fact]
    public void Remove_MissingAsset_ReturnsFalse()
    {
        var store = new FileModelStore(NewDir(), new List<ModelAsset>());
        Assert.False(store.Remove("nope.bin"));
    }

    [Fact]
    public void Missing_UsesPinnedChatterboxCatalogByDefault()
    {
        var store = new FileModelStore(NewDir());
        Assert.Equal(10, store.Missing().Count);
    }

    [Fact]
    public void SizeWithinTolerance_FivePercentBand()
    {
        Assert.True(FileModelStore.SizeWithinTolerance(100, 100));
        Assert.True(FileModelStore.SizeWithinTolerance(96, 100));   // ceil(5) band inclusive
        Assert.True(FileModelStore.SizeWithinTolerance(105, 100));
        Assert.False(FileModelStore.SizeWithinTolerance(94, 100));
        Assert.False(FileModelStore.SizeWithinTolerance(106, 100));
    }

    private static IReadOnlyList<ModelAsset> SmallCatalog() =>
    [
        new("a", "a.bin", 100),
        new("b", "b.bin", 100),
        new("c", "c.bin", 100),
    ];

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aiva-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
