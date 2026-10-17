namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Settings, in the order a player needs it: Voice quality, Processor, Downloads, Keybinds, then a
/// collapsed Advanced. Each part is its own small class. A nav anchor (<see cref="SettingsAnchors"/>)
/// scrolls to its part, and opens Advanced when the part is inside it.
/// </summary>
public sealed class SettingsPage : IPage
{
    private readonly SettingsInputs inputs;
    private readonly QualityCard quality;
    private readonly ProcessorCard processor;
    private readonly DownloadsCard downloads;
    private readonly KeybindsCard keybinds;
    private readonly AdvancedSection advanced;
    private readonly UiContext ctx;

    public SettingsPage(UiContext ctx)
    {
        this.ctx = ctx;
        this.inputs = new SettingsInputs(ctx);
        this.quality = new QualityCard(ctx, this.inputs);
        this.processor = new ProcessorCard(ctx, new ProcessorActions(ctx));
        this.downloads = new DownloadsCard(ctx, this.inputs);
        this.keybinds = new KeybindsCard(ctx);
        this.advanced = new AdvancedSection(ctx, this.inputs);
    }

    public NavTab Tab => NavTab.Settings;

    public void Draw()
    {
        var anchor = this.ctx.Nav.ConsumeAnchor(NavTab.Settings);
        this.inputs.Refresh();

        ScrollTo(anchor, SettingsAnchors.Quality);
        this.quality.Draw();
        ScrollTo(anchor, SettingsAnchors.Processor);
        this.processor.Draw();
        ScrollTo(anchor, SettingsAnchors.Downloads);
        this.downloads.Draw();
        ScrollTo(anchor, SettingsAnchors.Keybinds);
        this.keybinds.Draw();
        if (SettingsAnchors.InsideAdvanced(anchor))
        {
            ImGui.SetScrollHereY(0f);
        }

        this.advanced.Draw(anchor);
    }

    private static void ScrollTo(string? anchor, string key)
    {
        if (anchor == key)
        {
            ImGui.SetScrollHereY(0f);
        }
    }
}
