namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// What the Settings cards need that costs real IO (free disk, partial downloads), recomputed about
/// once a second instead of per frame: the inputs the quality plan is made from, and the Downloads rows.
/// </summary>
internal sealed class SettingsInputs(UiContext ctx)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1);

    private double at = double.NegativeInfinity;

    public SetupInputs Setup { get; private set; } = new(false, long.MaxValue, []);

    public IReadOnlyList<DownloadRow> Rows { get; private set; } = [];

    public long FreeBytes => this.Setup.FreeDiskBytes;

    public void Refresh()
    {
        var now = ImGui.GetTime();
        if (now - this.at < Lifetime.TotalSeconds)
        {
            return;
        }

        this.at = now;
        var provisioner = ctx.Provisioner();
        var engines = new List<EngineDownload>();
        foreach (var id in new[] { ConfigMigrator.KokoroId, ConfigMigrator.TurboId })
        {
            if (ctx.EngineCatalog.Find(id) is not { } info)
            {
                continue;
            }

            var required = EngineParts.RequiredAssets(ctx, info);
            var missing = required.Where(a => !ctx.Probes.IsDownloaded(a)).ToList();
            engines.Add(new EngineDownload(
                info.Key,
                info.DisplayName,
                required.Sum(a => a.SizeBytes ?? 0),
                missing.Count == 0 ? 0 : provisioner.BytesNeeded(missing)));
        }

        var free = ctx.ModelStore().FreeBytes(ctx.ModelsDir());
        this.Setup = new SetupInputs(
            ctx.DeviceInfo().HasUsableGpu,
            free,
            engines,
            QualityTierModel.FromConfig(ctx.Config.Engine, ctx.EngineCatalog));
        this.Rows = DownloadsModel.Rows(
            ctx.EngineCatalog.List(),
            ctx.ModelAssets(),
            ctx.Probes.IsDownloaded,
            provisioner.BytesNeeded,
            this.EngineInUse);
    }

    /// <summary>The engine is the main one, or the expressive one while expressive routing is on.</summary>
    public bool EngineInUse(string engineKey)
    {
        var settings = ctx.Config.Engine;
        if (ctx.PrimaryKey == engineKey)
        {
            return true;
        }

        return settings.Expressive.Enabled
               && ctx.PrimaryKey == ConfigMigrator.KokoroId
               && ctx.EngineCatalog.Resolve(settings.Expressive.EngineId) == engineKey;
    }
}
