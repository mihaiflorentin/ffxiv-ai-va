namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>Reads the live world into a <see cref="DiagnosticsInput"/> and copies the report; shared by the Processor banner and Advanced.</summary>
internal static class DiagnosticsSource
{
    public static string Report(UiContext ctx)
    {
        var tier = QualityTierModel.FromConfig(ctx.Config.Engine, ctx.EngineCatalog);
        var engines = new List<DiagnosticsEngine>();
        var gpu = SettingsParts.GpuEngine(ctx);
        foreach (var info in ctx.EngineCatalog.List().Where(e => !e.Retired))
        {
            EngineStatus? status = null;
            if (info.Key == ctx.PrimaryKey)
            {
                status = ctx.Synth.Status;
            }
            else if (gpu is { } g && g.Info.Key == info.Key)
            {
                status = g.Synth.Status;
            }

            var installed = info.AssetGroups.Count == 0 || SettingsParts.EngineInstalled(ctx, info);
            engines.Add(new DiagnosticsEngine(info.DisplayName, status, installed));
        }

        return DiagnosticsReport.Build(new DiagnosticsInput(
            typeof(DiagnosticsSource).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            ctx.Config.Version,
            tier is { } t ? QualityTierModel.Label(t) : "Custom",
            ctx.Config.Device,
            ctx.ActiveEngine.DisplayName,
            engines,
            ctx.DeviceProbe?.LastReport,
            ctx.Downloads.Active,
            ctx.Probes.ModelsFolderBytes,
            ctx.ModelStore().FreeBytes(ctx.ModelsDir()),
            ctx.Queue.Depth,
            ctx.Probes.ProcessWorkingSetBytes));
    }

    public static void CopyToClipboard(UiContext ctx)
    {
        ImGui.SetClipboardText(Report(ctx));
        ctx.Report("Details copied. Paste them into your bug report.", isError: false);
    }
}
