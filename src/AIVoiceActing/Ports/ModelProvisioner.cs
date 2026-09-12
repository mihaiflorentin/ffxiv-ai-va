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

    Task DownloadAsync(ModelAsset asset, IProgress<DownloadProgress> progress, CancellationToken cancellationToken);
}
