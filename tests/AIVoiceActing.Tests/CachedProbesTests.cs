namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class CachedProbesTests
{
    private static readonly ModelAsset A = new("Alpha", "a.bin", SizeBytes: 10);
    private static readonly ModelAsset B = new("Beta", "b.bin", SizeBytes: 20);

    private DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly HashSet<string> present = [];
    private int downloadedChecks;
    private int memoryReads;
    private int folderWalks;
    private long memory = 100;
    private long folder = 5;
    private readonly List<Action> queued = [];

    private CachedProbes Create() => new(
        () => this.now,
        () => [A, B],
        asset =>
        {
            this.downloadedChecks++;
            return this.present.Contains(asset.FileName);
        },
        () =>
        {
            this.memoryReads++;
            return this.memory;
        },
        () =>
        {
            this.folderWalks++;
            return this.folder;
        },
        this.queued.Add);

    private void RunBackground()
    {
        foreach (var work in this.queued.ToArray())
        {
            work();
        }

        this.queued.Clear();
    }

    [Fact]
    public void FirstTick_ProbesEverything()
    {
        var probes = this.Create();
        this.present.Add("a.bin");

        probes.Tick();
        this.RunBackground();

        Assert.True(probes.IsDownloaded(A));
        Assert.False(probes.IsDownloaded(B));
        Assert.Equal(100, probes.ProcessWorkingSetBytes);
        Assert.Equal(5, probes.ModelsFolderBytes);
    }

    [Fact]
    public void BeforeFirstTick_AssetsReadAsMissing()
    {
        var probes = this.Create();
        this.present.Add("a.bin");
        Assert.False(probes.IsDownloaded(A));
    }

    [Fact]
    public void DownloadedState_RefreshesAtMostOncePerSecond()
    {
        var probes = this.Create();
        probes.Tick();
        Assert.Equal(2, this.downloadedChecks);

        this.present.Add("b.bin");
        this.now += TimeSpan.FromMilliseconds(999);
        probes.Tick();
        Assert.Equal(2, this.downloadedChecks);
        Assert.False(probes.IsDownloaded(B));

        this.now += TimeSpan.FromMilliseconds(1);
        probes.Tick();
        Assert.Equal(4, this.downloadedChecks);
        Assert.True(probes.IsDownloaded(B));
    }

    [Fact]
    public void ProcessMemory_RefreshesAtMostOncePerSecond()
    {
        var probes = this.Create();
        probes.Tick();
        this.memory = 200;
        this.now += TimeSpan.FromMilliseconds(500);
        probes.Tick();
        Assert.Equal(1, this.memoryReads);
        Assert.Equal(100, probes.ProcessWorkingSetBytes);

        this.now += TimeSpan.FromMilliseconds(500);
        probes.Tick();
        Assert.Equal(2, this.memoryReads);
        Assert.Equal(200, probes.ProcessWorkingSetBytes);
    }

    [Fact]
    public void FolderSize_IsMeasuredOffThread_EveryTenSeconds()
    {
        var probes = this.Create();
        probes.Tick();
        Assert.Equal(0, this.folderWalks); // queued, not run on the draw thread
        Assert.Single(this.queued);
        this.RunBackground();
        Assert.Equal(5, probes.ModelsFolderBytes);

        this.folder = 9;
        this.now += TimeSpan.FromSeconds(9);
        probes.Tick();
        Assert.Empty(this.queued);

        this.now += TimeSpan.FromSeconds(1);
        probes.Tick();
        Assert.Single(this.queued);
        this.RunBackground();
        Assert.Equal(9, probes.ModelsFolderBytes);
        Assert.Equal(2, this.folderWalks);
    }

    [Fact]
    public void ManyTicksInOneInstant_ProbeOnce()
    {
        var probes = this.Create();
        for (var i = 0; i < 50; i++)
        {
            probes.Tick();
        }

        Assert.Equal(2, this.downloadedChecks);
        Assert.Equal(1, this.memoryReads);
        Assert.Single(this.queued);
    }
}
