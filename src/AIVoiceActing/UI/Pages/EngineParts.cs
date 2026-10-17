namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
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

    /// <summary>
    /// The assets an engine cannot run without (optional ones such as other precisions excluded). Turbo is the
    /// exception: it needs its base files plus one precision set, so the set it already has counts, and a
    /// machine with none is asked for the one it should download (see <see cref="TurboInstall"/>).
    /// </summary>
    public static List<ModelAsset> RequiredAssets(UiContext ctx, EngineInfo engine)
    {
        var groups = Groups(engine);
        if (groups.Contains(TurboInstall.BaseGroup))
        {
            var all = ctx.ModelAssets();
            var needed = TurboInstall.RequiredGroups(ctx.DeviceInfo().HasUsableGpu, GroupInstalled(ctx, all));
            return [.. all.Where(a => needed.Contains(a.Group)).Select(a => a.Asset)];
        }

        return [.. ctx.ModelAssets()
            .Where(a => groups.Contains(a.Group) && !a.Asset.Optional)
            .Select(a => a.Asset)];
    }

    /// <summary>True when the group has files in the catalog and every one is downloaded.</summary>
    public static Func<string, bool> GroupInstalled(UiContext ctx, IReadOnlyList<(ModelAsset Asset, string Group)> all) =>
        group =>
        {
            var files = all.Where(a => a.Group == group).ToList();
            return files.Count > 0 && files.All(a => ctx.Probes.IsDownloaded(a.Asset));
        };

    /// <summary>
    /// Size and missing bytes of each Turbo download group (base files, full-size set, q4f16 set): what the
    /// setup advisor plans with. Partial files already on disk are subtracted.
    /// </summary>
    public static List<GroupStatus> TurboGroupStatuses(UiContext ctx)
    {
        var provisioner = ctx.Provisioner();
        var all = ctx.ModelAssets();
        var statuses = new List<GroupStatus>();
        foreach (var group in new[] { TurboInstall.BaseGroup, TurboInstall.Fp32Group, TurboInstall.Q4F16Group })
        {
            var files = all.Where(a => a.Group == group).Select(a => a.Asset).ToList();
            var missing = files.Where(a => !ctx.Probes.IsDownloaded(a)).ToList();
            statuses.Add(new GroupStatus(
                group,
                files.Sum(a => a.SizeBytes ?? 0),
                missing.Count == 0 ? 0 : provisioner.BytesNeeded(missing)));
        }

        return statuses;
    }

    /// <summary>Required assets of the selected engine that are not downloaded yet.</summary>
    public static List<ModelAsset> MissingRequired(UiContext ctx) =>
        [.. RequiredAssets(ctx, ctx.ActiveEngine).Where(a => !ctx.Probes.IsDownloaded(a))];
}
