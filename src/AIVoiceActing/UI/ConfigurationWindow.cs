namespace AIVoiceActing.UI;

using System.Numerics;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.Pages;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

/// <summary>
/// Four-tab settings shell: Home / Voices / What's read / Settings. It owns only the tab bar, the
/// scrolling page host, the always-visible footer and the modal host; everything else lives in the
/// pages, which receive a <see cref="UiContext"/>. Driving adapter: pages read and mutate
/// <see cref="Configuration"/> and call container-resolved ports only.
/// Per-frame file/process probes are banned: the draw thread reads <see cref="CachedProbes"/>.
/// </summary>
public sealed class ConfigurationWindow : Window
{
    // Window proportions in em.
    private const float MinWidthEm = 36f;
    private const float MinHeightEm = 22f;
    private const float DefaultWidthEm = 46f;
    private const float DefaultHeightEm = 34f;

    private readonly UiContext ctx;
    private readonly IReadOnlyList<IPage> pages;
    private readonly FooterBar footer;

    public ConfigurationWindow(UiContext ctx)
        : base("AI Voice Acting Settings###AIVAConfig")
    {
        this.ctx = ctx;
        this.footer = new FooterBar(ctx);
        this.pages =
        [
            new HomePage(ctx),
            new VoicesPage(ctx),
            new ReadingPage(ctx),
            new SettingsPage(ctx),
        ];
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw()
    {
        this.WindowName =
            $"AI Voice Acting Settings (TTS {(this.ctx.Config.Enabled ? "Enabled" : "Disabled")})###AIVAConfig";

        // Sizes are measured in em (the font size already includes GlobalScale), so the window
        // holds its proportions from 100% to 200% scale.
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Ui.Em(MinWidthEm, MinHeightEm),
            MaximumSize = new Vector2(float.MaxValue),
        };
        this.Size = Ui.Em(DefaultWidthEm, DefaultHeightEm);
    }

    public override void Draw()
    {
        this.ctx.Probes.Tick();
        this.ctx.Preview.Tick();

        this.DrawTabBar();
        var page = this.pages.First(p => p.Tab == this.ctx.Nav.Selected);
        if (ImGui.BeginChild("##aiva-page", new Vector2(Ui.Fill(), -FooterBar.Height), false))
        {
            page.Draw();
        }

        ImGui.EndChild();
        this.footer.Draw();

        // Once per frame at window level, outside every child and table, so each modal's
        // OpenPopup and BeginPopupModal see the same ID stack.
        this.ctx.Modals.Draw();
    }

    /// <summary>Tabs without bodies: they only pick the page; the host child below draws it.</summary>
    private void DrawTabBar()
    {
        if (!ImGui.BeginTabBar("##aiva-tabs"))
        {
            return;
        }

        var forced = this.ctx.Nav.TakeForcedTab();
        foreach (var page in this.pages)
        {
            var flags = forced == page.Tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem(NavModel.Label(page.Tab), flags))
            {
                // While a programmatic jump is pending, tabs that are still the old selection
                // must not pull the selection back.
                if (forced is null || forced == page.Tab)
                {
                    this.ctx.Nav.Select(page.Tab);
                }

                ImGui.EndTabItem();
            }
        }

        ImGui.EndTabBar();
    }
}
