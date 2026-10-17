namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using Dalamud.Bindings.ImGui;

/// <summary>The key combination that mutes or unmutes every voice at once.</summary>
internal sealed class KeybindsCard(UiContext ctx)
{
    public void Draw()
    {
        var c = ctx.Config;
        SectionHeader.Draw("Keybinds");
        using var card = Card.Begin("settings-keybinds");
        if (!card.Visible)
        {
            return;
        }

        SettingsParts.Toggle(
            "Mute and unmute with a key combination",
            "Press the keys below to silence every voice at once, and again to bring them back.",
            () => c.UseKeybind,
            v => { c.UseKeybind = v; ctx.Save(); });
        if (!c.UseKeybind)
        {
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Keys");
        ImGui.SameLine(Ui.Em(FormRow.LabelColumnEm));
        ImGui.SetNextItemWidth(Ui.Em(6f));
        Controls.KeyCombo("##settings-key-modifier", KeyOptions.Modifiers, () => c.ModifierKey, v => { c.ModifierKey = v; ctx.Save(); });
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("+");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Ui.Em(6f));
        Controls.KeyCombo("##settings-key-main", KeyOptions.MajorKeys, () => c.MajorKey, v => { c.MajorKey = v; ctx.Save(); });
        SettingsParts.RowHelp("The first key is held down while the second is pressed.");
    }
}
