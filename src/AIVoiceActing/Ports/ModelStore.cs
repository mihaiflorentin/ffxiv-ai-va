namespace AIVoiceActing.Ports;

/// <summary>Thrown when a required model asset has not been downloaded yet.</summary>
public sealed class ModelNotDownloadedException(string message) : Exception(message);

/// <summary>Thrown before a download starts when the target volume lacks the space it needs.</summary>
public sealed class InsufficientDiskSpaceException(long requiredBytes, long availableBytes)
    : Exception($"Not enough free disk space: {requiredBytes} bytes needed, {availableBytes} available.")
{
    public long RequiredBytes { get; } = requiredBytes;

    public long AvailableBytes { get; } = availableBytes;
}

/// <summary>A downloadable model asset tracked by the catalog.</summary>
/// <param name="Name">Human-readable asset name for UI rows.</param>
/// <param name="FileName">Relative file name under the models directory.</param>
/// <param name="SizeBytes">Expected download size in bytes, for progress and sanity checks; null if unknown.</param>
/// <param name="Optional">True for optional assets (e.g. the LLM emotion director); missing optionals never block readiness.</param>
/// <param name="Precision">Numeric precision of the weights ("fp32", "fp16", "q4", "q4f16"); "n/a" for non-model files (tokenizers, voices); null when unspecified.</param>
public sealed record ModelAsset(
    string Name,
    string FileName,
    long? SizeBytes = null,
    bool Optional = false,
    string? Precision = null);

/// <summary>Driven port over the on-disk model store.</summary>
public interface IModelStore
{
    /// <summary>Absolute directory holding downloaded model assets.</summary>
    string ModelsDir { get; }

    /// <summary>True when the named asset is present on disk.</summary>
    bool IsDownloaded(string assetName);

    /// <summary>Free bytes on the volume holding <paramref name="dir"/> (nearest existing ancestor);
    /// <see cref="long.MaxValue"/> when the volume cannot be queried, so unknown never blocks a download.</summary>
    long FreeBytes(string dir);

    /// <summary>Required assets not yet downloaded (optionals excluded).</summary>
    IReadOnlyList<ModelAsset> Missing();

    /// <summary>Deletes a downloaded asset (and any stale .part twin). True when a file was removed.</summary>
    bool Remove(string assetName);
}
