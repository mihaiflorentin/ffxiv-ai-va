namespace AIVoiceActing.Ports;

/// <summary>Download progress snapshot for one asset.</summary>
/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">Expected total, when known.</param>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>Completion percentage, 0..100, when the total is known.</summary>
    public double? Percent => this.TotalBytes is > 0 ? this.BytesReceived * 100.0 / this.TotalBytes.Value : null;
}

/// <summary>Driven port downloading model assets on explicit user request.</summary>
public interface IModelProvisioner
{
    /// <summary>True when the asset is already present and passes size sanity checks.</summary>
    bool IsDownloaded(ModelAsset asset);

    /// <summary>
    /// Downloads one asset. Resumes a partial ".part" file with an HTTP Range request (a server
    /// that ignores Range restarts cleanly) and throws <see cref="InsufficientDiskSpaceException"/>
    /// when the remaining bytes do not fit on disk. A cancelled or interrupted download keeps its
    /// partial file; a size or hash mismatch deletes it.
    /// </summary>
    Task DownloadAsync(ModelAsset asset, IProgress<DownloadProgress> progress, CancellationToken cancellationToken);

    /// <summary>Bytes still to fetch for <paramref name="assets"/> (missing files minus any resumable partials).</summary>
    long BytesNeeded(IReadOnlyList<ModelAsset> assets);

    /// <summary>
    /// Downloads every missing asset in order. Checks free disk space for the whole set first and
    /// throws <see cref="InsufficientDiskSpaceException"/> before fetching anything when it is short.
    /// </summary>
    Task DownloadGroupAsync(
        IReadOnlyList<ModelAsset> assets,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);
}
