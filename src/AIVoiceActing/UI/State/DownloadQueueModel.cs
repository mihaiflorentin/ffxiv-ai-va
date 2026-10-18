namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>
/// Sequential background download queue, extracted from the settings window. Assets download
/// one at a time in list order and <see cref="Current"/> never reads null between items, so
/// the draw thread's idle gating stays exact. The queue stops at the first failure (a sticky
/// error with a Retry action goes to the <see cref="StatusCenter"/>) and is cancellable.
/// Counters are written from the worker and read on the draw thread.
/// </summary>
public sealed class DownloadQueueModel
{
    public const string FailureKey = "download-failed";

    private readonly object gate = new();
    private readonly Func<IModelProvisioner> provisioner;
    private readonly StatusCenter status;
    private readonly Action<string> logInfo;
    private readonly Action<string, Exception?> logError;

    private volatile ModelAsset? current;
    private long received;
    private long total;
    private volatile int fileTotal;
    private volatile int fileDone;
    private CancellationTokenSource? cts;

    public DownloadQueueModel(
        Func<IModelProvisioner> provisioner,
        StatusCenter status,
        Action<string>? logInfo = null,
        Action<string, Exception?>? logError = null)
    {
        this.provisioner = provisioner;
        this.status = status;
        this.logInfo = logInfo ?? (_ => { });
        this.logError = logError ?? ((_, _) => { });
    }

    /// <summary>The asset downloading right now; null when idle.</summary>
    public ModelAsset? Current => this.current;

    public bool Active => this.current is not null;

    public long Received => Interlocked.Read(ref this.received);

    /// <summary>Expected size of the current asset (0 when unknown).</summary>
    public long Total => Interlocked.Read(ref this.total);

    /// <summary>Files in the running queue (0 when idle).</summary>
    public int FileTotal => this.fileTotal;

    /// <summary>Files finished in the running queue.</summary>
    public int FileDone => this.fileDone;

    /// <summary>The running (or last) queue task; tests await it.</summary>
    public Task? Completion { get; private set; }

    /// <summary>Progress fraction 0..1 of the current file; 0 when the size is unknown.</summary>
    public double Fraction => ModelsTabModel.OverallProgress(this.Received, this.Total <= 0 ? null : this.Total);

    /// <summary>Progress-bar caption: which file of how many plus the byte progress.</summary>
    public string Caption
    {
        get
        {
            var asset = this.current;
            if (asset is null)
            {
                return string.Empty;
            }

            var total = this.Total;
            return ModelsTabModel.QueuePositionLabel(
                asset.Name,
                this.fileDone + 1,
                Math.Max(this.fileTotal, this.fileDone + 1),
                this.Received,
                total <= 0 ? null : total);
        }
    }

    /// <summary>Starts the queue; false when it is empty or one is already running.</summary>
    public bool Start(IReadOnlyList<ModelAsset> assets)
    {
        lock (this.gate)
        {
            if (assets.Count == 0 || this.current is not null)
            {
                return false;
            }

            this.cts?.Dispose();
            var source = this.cts = new CancellationTokenSource();
            this.fileTotal = assets.Count;
            this.fileDone = 0;
            this.current = assets[0];
            this.ResetProgress(assets[0]);
            this.status.Resolve(FailureKey);
            this.logInfo($"Download queue started: {assets.Count} file(s): {string.Join(", ", assets.Select(a => a.Name))}.");
            var pending = assets.ToArray();
            this.Completion = Task.Run(() => this.RunAsync(pending, source));
            return true;
        }
    }

    /// <summary>Cancels the running queue; a no-op when idle.</summary>
    public void Cancel()
    {
        lock (this.gate)
        {
            try
            {
                this.cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Queue settled between draw and click.
            }
        }
    }

    private async Task RunAsync(ModelAsset[] assets, CancellationTokenSource source)
    {
        var provisioner = this.provisioner();
        for (var i = 0; i < assets.Length; i++)
        {
            var asset = assets[i];
            this.current = asset;
            this.ResetProgress(asset);
            this.logInfo($"Download started: {asset.Name} (file {this.fileDone + 1} of {Math.Max(this.fileTotal, this.fileDone + 1)}).");
            try
            {
                await provisioner.DownloadAsync(
                    asset,
                    new SyncProgress(p =>
                    {
                        Interlocked.Exchange(ref this.received, p.BytesReceived);
                        if (p.TotalBytes is { } size)
                        {
                            Interlocked.Exchange(ref this.total, size);
                        }
                    }),
                    source.Token);
            }
            catch (OperationCanceledException)
            {
                this.Settle(source);
                this.logInfo($"Download cancelled at \"{asset.Name}\".");
                this.status.Post(Severity.Info, "Download cancelled.");
                return;
            }
            catch (Exception ex)
            {
                this.Settle(source);
                this.logError($"Download of \"{asset.Name}\" failed: {ex.Message}", ex);
                var remaining = assets[i..];
                this.status.Post(
                    Severity.Error,
                    $"Download of \"{asset.Name}\" failed",
                    ex.Message,
                    [new StatusAction("Retry", () => this.Start(remaining))],
                    key: FailureKey);
                return;
            }

            this.fileDone = i + 1;
            this.logInfo($"Download finished: {asset.Name}.");
            this.status.Post(Severity.Success, $"Downloaded {asset.Name}.");
        }

        this.Settle(source);
    }

    private void ResetProgress(ModelAsset asset)
    {
        Interlocked.Exchange(ref this.received, 0);
        Interlocked.Exchange(ref this.total, asset.SizeBytes ?? 0);
    }

    private void Settle(CancellationTokenSource source)
    {
        lock (this.gate)
        {
            this.current = null;
            this.fileTotal = 0;
            Interlocked.Exchange(ref this.received, 0);
            Interlocked.Exchange(ref this.total, 0);
            if (ReferenceEquals(this.cts, source))
            {
                this.cts = null;
            }

            source.Dispose();
        }
    }

    /// <summary>Progress that reports on the calling thread (the framework's Progress{T} posts asynchronously).</summary>
    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
