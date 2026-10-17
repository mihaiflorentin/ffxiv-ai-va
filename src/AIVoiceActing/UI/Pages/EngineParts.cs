namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;

/// <summary>
/// Asset-group helpers shared by Home (setup flow) and Settings (quality, downloads). Group keys
/// come from the engine catalog port, not from string literals per engine.
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
}
