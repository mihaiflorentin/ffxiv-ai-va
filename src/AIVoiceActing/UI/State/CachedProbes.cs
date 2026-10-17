namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>
/// Cached environment probes for the draw thread: which assets are downloaded, process memory,
/// and the models-folder size. The window calls <see cref="Tick"/> once per frame; each probe
/// re-runs at most once per its interval (the folder walk off the draw thread), so no file or
/// process probing happens per frame. Clock, IO and the background runner are injected.
/// </summary>
public sealed class CachedProbes
{
    public static readonly TimeSpan DownloadedInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan FolderInterval = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> clock;
    private readonly Func<IEnumerable<ModelAsset>> assets;
    private readonly Func<ModelAsset, bool> isDownloaded;
    private readonly Func<long> readProcessMemory;
    private readonly Func<long> measureFolder;
    private readonly Action<Action> background;

    private Dictionary<string, bool> downloaded = new(StringComparer.Ordinal);
    private DateTimeOffset? downloadedAt;
    private DateTimeOffset? memoryAt;
    private DateTimeOffset? folderAt;
    private long processBytes;
    private long folderBytes;

    public CachedProbes(
        Func<DateTimeOffset> clock,
        Func<IEnumerable<ModelAsset>> assets,
        Func<ModelAsset, bool> isDownloaded,
        Func<long> readProcessMemory,
        Func<long> measureFolder,
        Action<Action>? background = null)
    {
        this.clock = clock;
        this.assets = assets;
        this.isDownloaded = isDownloaded;
        this.readProcessMemory = readProcessMemory;
        this.measureFolder = measureFolder;
        this.background = background ?? (work => _ = Task.Run(work));
    }

    /// <summary>Working set of the game process, bytes (sampled).</summary>
    public long ProcessWorkingSetBytes => Interlocked.Read(ref this.processBytes);

    /// <summary>Size of the models folder, bytes (sampled off-thread).</summary>
    public long ModelsFolderBytes => Interlocked.Read(ref this.folderBytes);

    /// <summary>Cached download state of <paramref name="asset"/>; false until the first refresh sees it.</summary>
    public bool IsDownloaded(ModelAsset asset) => this.downloaded.GetValueOrDefault(asset.FileName);

    /// <summary>Refreshes whichever probes are due. Call once per frame.</summary>
    public void Tick()
    {
        var now = this.clock();

        if (Due(this.downloadedAt, now, DownloadedInterval))
        {
            this.downloadedAt = now;
            var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var asset in this.assets())
            {
                cache[asset.FileName] = this.isDownloaded(asset);
            }

            this.downloaded = cache;
        }

        if (Due(this.memoryAt, now, MemoryInterval))
        {
            this.memoryAt = now;
            Interlocked.Exchange(ref this.processBytes, this.readProcessMemory());
        }

        if (Due(this.folderAt, now, FolderInterval))
        {
            this.folderAt = now;
            this.background(() => Interlocked.Exchange(ref this.folderBytes, this.measureFolder()));
        }
    }

    private static bool Due(DateTimeOffset? last, DateTimeOffset now, TimeSpan interval) =>
        last is null || now - last.Value >= interval;
}
