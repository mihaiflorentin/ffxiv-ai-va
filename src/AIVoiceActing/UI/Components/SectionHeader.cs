namespace AIVoiceActing.UI.Components;

using System.Numerics;
using Dalamud.Bindings.ImGui;

/// <summary>Accent-coloured section label followed by a thin rule to the right edge.</summary>
public static class SectionHeader
{
    public static void Draw(string label)
    {
        Ui.Gap(Ui.Space2);
        using (Theme.PushText(Theme.Accent))
        {
            ImGui.TextUnformatted(label);
        }

        // SeparatorText is absent from these bindings, so draw the rule by hand.
        var min = ImGui.GetItemRectMax();
        var mid = min.Y - (ImGui.GetItemRectSize().Y * 0.5f);
        var start = new Vector2(min.X + Ui.Em(Ui.Space2), mid);
        var end = new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X, mid);
        if (end.X > start.X)
        {
            ImGui.GetWindowDrawList().AddLine(start, end, ImGui.GetColorU32(ImGuiCol.Border), Ui.Scaled(1f));
        }

        Ui.Gap(Ui.Space1);
    }
}
