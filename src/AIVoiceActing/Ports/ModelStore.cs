namespace AIVoiceActing.Ports;

/// <summary>Thrown when a required model asset has not been downloaded yet.</summary>
public sealed class ModelNotDownloadedException(string message) : Exception(message);

/// <summary>A downloadable model asset tracked by the catalog.</summary>
/// <param name="Name">Human-readable asset name for UI rows.</param>
/// <param name="FileName">Relative file name under the models directory.</param>
/// <param name="SizeBytes">Expected download size in bytes, for progress and sanity checks; null if unknown.</param>
/// <param name="Optional">True for optional assets (e.g. the LLM emotion director); missing optionals never block readiness.</param>
public sealed record ModelAsset(
    string Name,
    string FileName,
    long? SizeBytes = null,
    bool Optional = false);

/// <summary>Driven port over the on-disk model store.</summary>
public interface IModelStore
{
    /// <summary>Absolute directory holding downloaded model assets.</summary>
    string ModelsDir { get; }

    /// <summary>True when the named asset is present on disk.</summary>
    bool IsDownloaded(string assetName);

    /// <summary>Required assets not yet downloaded (optionals excluded).</summary>
    IReadOnlyList<ModelAsset> Missing();
}
