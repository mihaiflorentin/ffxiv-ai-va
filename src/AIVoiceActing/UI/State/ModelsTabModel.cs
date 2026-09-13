namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>
/// Pure Models-tab presentation state: one immutable row per catalog asset (name, size,
/// download state), the global download-progress snapshot, and the can-download gating the
/// Draw layer mirrors 1:1 with ImGui controls. Dalamud- and ImGui-free.
/// </summary>
public static class ModelsTabModel
{
    public sealed record AssetRow(
        string Name,
        string FileName,
        double SizeMb,
        bool Downloaded,
        bool Optional);

    public static AssetRow Row(ModelAsset asset, bool downloaded) => new(
        asset.Name,
        asset.FileName,
        asset.SizeBytes is { } bytes ? Math.Round(bytes / (1024d * 1024d), 1) : 0d,
        downloaded,
        asset.Optional);

    /// <summary>"downloaded" / "missing" / "optional (missing)"; the draw layer prefixes
    /// "downloading…" while a download is in flight.</summary>
    public static string StatusLabel(AssetRow row) =>
        row.Downloaded
            ? "downloaded"
            : row.Optional
                ? "optional (missing)"
                : "missing";

    /// <summary>A download may start only when idle and the asset is absent.</summary>
    public static bool CanDownload(bool downloadInFlight, AssetRow row) =>
        !downloadInFlight && !row.Downloaded;

    /// <summary>Label for the bulk Models-tab action.</summary>
    public static string DownloadAllLabel(int missingCount) =>
        missingCount > 0 ? $"Download all missing ({missingCount})" : "All required assets downloaded";

    /// <summary>The bulk action is clickable only when idle and required assets are missing.</summary>
    public static bool CanDownloadAll(bool downloadInFlight, int missingCount) =>
        !downloadInFlight && missingCount > 0;

    /// <summary>Progress fraction 0..1 for the overall bar; 0 when the total is unknown.</summary>
    public static double OverallProgress(long bytesReceived, long? totalBytes) =>
        totalBytes is > 0 ? Math.Clamp(bytesReceived / (double)totalBytes.Value, 0d, 1d) : 0d;

    /// <summary>"12.3 / 456.7 MB" style label for under the progress bar.</summary>
    public static string ProgressLabel(long bytesReceived, long? totalBytes) =>
        totalBytes is { } total
            ? $"{Mb(bytesReceived):0.0} / {Mb(total):0.0} MB"
            : $"{Mb(bytesReceived):0.0} MB";

    /// <summary>Disabled reason for the Speak/Test buttons (mirrored in the Test tab).</summary>
    public static string EngineNotReadyHint => "Models not downloaded — use the Models tab.";

    private static double Mb(long bytes) => bytes / (1024d * 1024d);
}
