namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Settings: the old Engine tab, Test tab, Keybinds and Experimental sections as collapsing
/// sections. A nav anchor ("engine", "test", "keybinds", "experimental") opens its section.
/// The Settings page ticket reshapes this into Quality / Processor / Downloads / Advanced.
/// </summary>
public sealed class SettingsPage(UiContext ctx) : IPage
{
    private readonly EnginePanel engine = new(ctx);
    private readonly TestPanel test = new(ctx);

    public NavTab Tab => NavTab.Settings;

    public void Draw()
    {
        var anchor = ctx.Nav.ConsumeAnchor(NavTab.Settings);
        var c = ctx.Config;
        var save = ctx.Save;

        if (Section("Engine", "engine", anchor, ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.engine.Draw();
        }

        if (Section("Test", "test", anchor, ImGuiTreeNodeFlags.None))
        {
            this.test.Draw();
        }

        if (Section("Keybinds", "keybinds", anchor, ImGuiTreeNodeFlags.None))
        {
            Controls.Checkbox("Use keybind to toggle TTS", () => c.UseKeybind, v => { c.UseKeybind = v; save(); },
                "Hold the keys below to mute or unmute all voice output at once.");
            if (c.UseKeybind)
            {
                ImGui.Indent();
                Controls.KeyCombo("Modifier", KeyOptions.Modifiers, () => c.ModifierKey, v => { c.ModifierKey = v; save(); }, "First key of the toggle combination.");
                Controls.KeyCombo("Key", KeyOptions.MajorKeys, () => c.MajorKey, v => { c.MajorKey = v; save(); }, "Second key of the toggle combination.");
                ImGui.Unindent();
            }
        }

        if (Section("Experimental", "experimental", anchor, ImGuiTreeNodeFlags.None))
        {
            Controls.Checkbox(
                "Remove stutter",
                "Strips repeated leading characters from stammering lines before synthesis.",
                () => c.RemoveStutter,
                v => { c.RemoveStutter = v; save(); });
        }
    }

    private static bool Section(string title, string anchorKey, string? anchor, ImGuiTreeNodeFlags flags)
    {
        if (anchor == anchorKey)
        {
            ImGui.SetNextItemOpen(true);
        }

        return ImGui.CollapsingHeader(title + "##settings-" + anchorKey, flags);
    }
}
