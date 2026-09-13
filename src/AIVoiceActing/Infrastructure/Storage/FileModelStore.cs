namespace AIVoiceActing.Infrastructure.Storage;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;

/// <summary>
/// On-disk model store over a plain directory (models/ in the repo, ConfigDirectory/models
/// in-game). An asset counts as downloaded when the file exists and its size is within
/// ±5% of the pinned size (truncated/corrupt downloads are re-fetchable).
/// </summary>
public sealed class FileModelStore : IModelStore
{
    /// <summary>Download-size sanity tolerance applied by the store and provisioner.</summary>
    public const double SizeTolerance = 0.05;

    private readonly string modelsDir;
    private readonly IReadOnlyList<ModelAsset> catalog;

    /// <param name="modelsDir">Absolute directory holding downloaded assets.</param>
    /// <param name="catalog">Assets to track; defaults to the pinned Chatterbox set.</param>
    public FileModelStore(string modelsDir, IReadOnlyList<ModelAsset>? catalog = null)
    {
        this.modelsDir = Path.GetFullPath(modelsDir);
        this.catalog = catalog ?? ModelCatalog.ChatterboxRequiredAssets;
        Directory.CreateDirectory(this.modelsDir);
    }

    public string ModelsDir => this.modelsDir;

    public bool IsDownloaded(string assetName)
    {
        var path = this.PathFor(assetName);
        if (!File.Exists(path))
        {
            return false;
        }

        var expected = this.catalog.FirstOrDefault(a => a.FileName == assetName)?.SizeBytes;
        return expected is not { } size || SizeWithinTolerance(new FileInfo(path).Length, size);
    }

    public IReadOnlyList<ModelAsset> Missing() =>
        this.catalog.Where(a => !a.Optional && !this.IsDownloaded(a.FileName)).ToArray();

    public bool Remove(string assetName)
    {
        var path = this.PathFor(assetName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        if (File.Exists(path + ".part"))
        {
            File.Delete(path + ".part");
        }

        return true;
    }

    public string PathFor(string assetName) => Path.Combine(this.modelsDir, assetName);

    public static bool SizeWithinTolerance(long actual, long expected) =>
        Math.Abs(actual - expected) <= (long)Math.Ceiling(expected * SizeTolerance);
}
