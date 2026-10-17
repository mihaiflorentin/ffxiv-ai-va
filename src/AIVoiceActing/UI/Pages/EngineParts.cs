namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Engine drawing shared by Home (status, get-started) and Settings (engine cards): the status
/// strip, the first-run guide, the download progress row, and the asset-group helpers. Group
/// keys come from the engine catalog port, not from string literals per engine.
/// </summary>
internal static class EngineParts
{
    /// <summary>The optional emotion classifier's download group (owned by the default engine in the catalog, listed apart in the UI).</summary>
    public const string EmotionGroup = "emotion";

    /// <summary>The engine's own download groups, without the shared emotion classifier.</summary>
    public static IReadOnlyList<string> Groups(EngineInfo engine) =>
        [.. engine.AssetGroups.Where(g => g != EmotionGroup)];

    /// <summary>Required assets of the selected engine that are not downloaded yet.</summary>
    public static List<ModelAsset> MissingRequired(UiContext ctx)
    {
        var groups = Groups(ctx.ActiveEngine);
        return [.. ctx.ModelAssets()
            .Where(a => groups.Contains(a.Group) && !a.Asset.Optional && !ctx.Probes.IsDownloaded(a.Asset))
            .Select(a => a.Asset)];
    }

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

    /// <summary>First-run guide, shown while the active engine is not installed: one button
    /// downloads exactly what the engine needs, then two short pointers.</summary>
    public static void DrawGetStarted(UiContext ctx, IReadOnlyList<ModelAsset> missing)
    {
        var anyDownload = ctx.Downloads.Active;
        var megabytes = missing.Sum(a => a.SizeBytes ?? 0) / (1024.0 * 1024.0);

        ImGui.Separator();
        using (Theme.PushText(Theme.Warn))
        {
            ImGui.TextUnformatted("Get started");
        }

        ImGui.TextWrapped($"1. Download the voice engine (about {megabytes:0} MB, once).");
        ImGui.SameLine();
        if (Controls.Button(
                "Download##get-started",
                !anyDownload && missing.Count > 0,
                anyDownload ? "A download is already in progress." : null))
        {
            StartDownloads(ctx, missing);
        }

        ImGui.TextWrapped("2. Talk to any NPC: each character gets a voice that fits their race, gender and age, and keeps it.");
        ImGui.TextWrapped("3. Optional: download the emotion reader in Settings for moodier delivery, and fine-tune voices on the Voices tab.");
        ImGui.Separator();
    }
}
