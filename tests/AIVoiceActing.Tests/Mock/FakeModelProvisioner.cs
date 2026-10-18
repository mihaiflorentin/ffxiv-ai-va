namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>
/// Fake IModelProvisioner (census fake pattern): records every download request in order. Each
/// download completes when the test releases it (<see cref="Release"/>), fails when
/// <see cref="FailOn"/> names its file, and honours cancellation like the real adapter.
/// </summary>
public sealed class FakeModelProvisioner : IModelProvisioner
{
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCompletionSource> gates = new(StringComparer.Ordinal);

    public List<string> Started { get; } = [];

    public HashSet<string> Present { get; } = new(StringComparer.Ordinal);

    public string? FailOn { get; set; }

    /// <summary>When true, downloads finish immediately instead of waiting for <see cref="Release"/>.</summary>
    public bool AutoComplete { get; set; }

    public long ReportBytes { get; set; }

    public bool IsDownloaded(ModelAsset asset) => this.Present.Contains(asset.FileName);

    public async Task DownloadAsync(ModelAsset asset, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs;
        lock (this.gate)
        {
            this.Started.Add(asset.FileName);
            tcs = this.GateFor(asset.FileName);
        }

        if (this.ReportBytes > 0)
        {
            progress.Report(new DownloadProgress(this.ReportBytes, asset.SizeBytes));
        }

        if (this.FailOn == asset.FileName)
        {
            throw new IOException("disk full");
        }

        if (!this.AutoComplete)
        {
            await tcs.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        this.Present.Add(asset.FileName);
    }

    public long BytesNeeded(IReadOnlyList<ModelAsset> assets) =>
        assets.Where(a => !this.IsDownloaded(a)).Sum(a => a.SizeBytes ?? 0);

    public async Task DownloadGroupAsync(IReadOnlyList<ModelAsset> assets, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        foreach (var asset in assets)
        {
            await this.DownloadAsync(asset, progress, cancellationToken);
        }
    }

    /// <summary>Lets the pending download of <paramref name="fileName"/> finish.</summary>
    public void Release(string fileName)
    {
        lock (this.gate)
        {
            this.GateFor(fileName).TrySetResult();
        }
    }

    /// <summary>Spins until the download has started (the queue runs on the thread pool).</summary>
    public async Task WaitUntilStarted(string fileName)
    {
        for (var i = 0; i < 400; i++)
        {
            lock (this.gate)
            {
                if (this.Started.Contains(fileName))
                {
                    return;
                }
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"{fileName} never started.");
    }

    private TaskCompletionSource GateFor(string fileName)
    {
        if (!this.gates.TryGetValue(fileName, out var tcs))
        {
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.gates[fileName] = tcs;
        }

        return tcs;
    }
}
