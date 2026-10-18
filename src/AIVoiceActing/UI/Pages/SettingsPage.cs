namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Settings, in the order a player needs it: Voice quality, Processor, Downloads, Keybinds, then a
/// collapsed Advanced. Each part is its own small class. A nav anchor (<see cref="SettingsAnchors"/>)
/// scrolls to its part, and opens Advanced when the part is inside it. The jump is repeated for a
/// few frames because cards that were never drawn settle on their real height one frame later.
/// </summary>
public sealed class SettingsPage : IPage
{
    private const int ScrollSettleFrames = 3;

    private readonly SettingsInputs inputs;
    private readonly QualityCard quality;
    private readonly ProcessorCard processor;
    private readonly DownloadsCard downloads;
    private readonly KeybindsCard keybinds;
    private readonly AdvancedSection advanced;
    private readonly UiContext ctx;
    private string? pendingAnchor;
    private int scrollFrames;

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
        var consumed = this.ctx.Nav.ConsumeAnchor(NavTab.Settings);
        if (consumed is not null)
        {
            this.pendingAnchor = consumed;
            this.scrollFrames = ScrollSettleFrames;
        }

        var anchor = this.scrollFrames > 0 ? this.pendingAnchor : null;
        this.inputs.Refresh();

        ScrollTo(anchor, SettingsAnchors.Quality);
        this.quality.Draw();
        ScrollTo(anchor, SettingsAnchors.Processor);
        this.processor.Draw();
        ScrollTo(anchor, SettingsAnchors.Downloads);
        this.downloads.Draw();
        ScrollTo(anchor, SettingsAnchors.Keybinds);
        this.keybinds.Draw();
        ScrollTo(anchor, SettingsAnchors.Advanced);
        this.advanced.Draw(anchor);
        if (this.scrollFrames > 0)
        {
            this.scrollFrames--;
        }
    }

    private static void ScrollTo(string? anchor, string key)
    {
        if (anchor == key)
        {
            ImGui.SetScrollHereY(0f);
        }
    }
}
