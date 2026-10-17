namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Engine drawing shared by Home (setup flow) and Settings (engine cards): the status strip, the
/// download progress row, and the asset-group helpers. Group keys come from the engine catalog
/// port, not from string literals per engine.
/// </summary>
internal static class EngineParts
{
    /// <summary>The optional emotion classifier's download group (owned by the default engine in the catalog, listed apart in the UI).</summary>
    public const string EmotionGroup = "emotion";

    /// <summary>The engine's own download groups, without the shared emotion classifier.</summary>
    public static IReadOnlyList<string> Groups(EngineInfo engine) =>
        [.. engine.AssetGroups.Where(g => g != EmotionGroup)];

    /// <summary>The assets an engine cannot run without (optional ones such as other precisions excluded).</summary>
    public static List<ModelAsset> RequiredAssets(UiContext ctx, EngineInfo engine)
    {
        var groups = Groups(engine);
        return [.. ctx.ModelAssets()
            .Where(a => groups.Contains(a.Group) && !a.Asset.Optional)
            .Select(a => a.Asset)];
    }

    /// <summary>Required assets of the selected engine that are not downloaded yet.</summary>
    public static List<ModelAsset> MissingRequired(UiContext ctx) =>
        [.. RequiredAssets(ctx, ctx.ActiveEngine).Where(a => !ctx.Probes.IsDownloaded(a))];

    public static void StartDownloads(UiContext ctx, IReadOnlyList<ModelAsset> assets) => ctx.Downloads.Start(assets);

    /// <summary>Status strip: cached fields only, never per-frame probes.</summary>
    public static void DrawStatusStrip(UiContext ctx)
    {
        var ready = ctx.Synth.IsReady;
        ImGui.TextUnformatted(
            $"Engine: {ctx.Config.Engine.Primary} — " +
            (ctx.EngineSession.Warming ? "loading…"
                : ready ? "ready"
                : $"NOT ready — {ctx.EngineReason()}"));
        ImGui.TextDisabled(
            $"Queue: {ctx.Queue.Depth}   Models folder: {ctx.Probes.ModelsFolderBytes / (1024.0 * 1024.0):0} MB   " +
            $"Process memory: {ctx.Probes.ProcessWorkingSetBytes / (1024.0 * 1024.0):0} MB");
    }

    /// <summary>Progress bar + Cancel while a download queue runs.</summary>
    public static void DrawDownloadProgress(UiContext ctx)
    {
        if (!ctx.Downloads.Active)
        {
            return;
        }

        ImGui.ProgressBar(
            (float)ctx.Downloads.Fraction,
            new System.Numerics.Vector2(Ui.Fill() - Ui.Em(8), 0f),
            ctx.Downloads.Caption);
        ImGui.SameLine();
        if (Controls.Button("Cancel download", enabled: true, null))
        {
            ctx.Downloads.Cancel();
        }
    }
}
