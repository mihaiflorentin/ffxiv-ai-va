namespace AIVoiceActing.UI.Components;

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

/// <summary>Compact pill: a toggleable filter chip, or a read-only tag.</summary>
public static class Chip
{
    /// <summary>Filter chip. Returns true when clicked; the caller flips its own state.</summary>
    public static bool Toggle(string label, bool selected)
    {
        using var shape = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui.Em(1f))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(Ui.Em(0.7f), Ui.Em(0.2f)));
        if (selected)
        {
            return Theme.PrimaryButton(label);
        }

        using var plain = ImRaii.PushColor(ImGuiCol.Button, Theme.Card);
        return ImGui.Button(label);
    }

    /// <summary>Read-only tag, for example the "Duplicated" or "Edited" markers on a row.</summary>
    public static void Tag(string label, Vector4? colour = null)
    {
        using var c = Theme.PushText(colour ?? Theme.Muted);
        ImGui.TextUnformatted("[" + label + "]");
    }
}
