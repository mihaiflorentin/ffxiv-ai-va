namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Home: the master switch and volume (moved from the old Chat tab), the engine status strip,
/// the first-run guide while the engine is missing, and the running download. The Home redesign
/// (one status line, "Try it", setup button) lands in the Home page ticket.
/// </summary>
public sealed class HomePage(UiContext ctx) : IPage
{
    public NavTab Tab => NavTab.Home;

    public void Draw()
    {
        var c = ctx.Config;
        var save = ctx.Save;

        Controls.Checkbox("Enabled", () => c.Enabled, v => { c.Enabled = v; save(); },
            "Master switch: unchecked stops all synthesis and playback.");
        Controls.SliderScaled("Global volume %", 200f, () => c.GlobalVolume, v => c.GlobalVolume = v, save,
            "Playback volume; above 100% boosts quiet voices. 100% plays clips as synthesized.");
        ImGui.Separator();

        EngineParts.DrawStatusStrip(ctx);

        var missing = EngineParts.MissingRequired(ctx);
        if (missing.Count > 0)
        {
            EngineParts.DrawGetStarted(ctx, missing);
        }

        EngineParts.DrawDownloadProgress(ctx);
    }
}
