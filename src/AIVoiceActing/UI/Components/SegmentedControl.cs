namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

/// <summary>Joined buttons for a short either-or choice (for example Fast / Balanced / Most expressive).</summary>
public static class SegmentedControl
{
    /// <summary>Returns the selected index (the clicked one, or <paramref name="selected"/> unchanged).</summary>
    public static int Draw(string id, IReadOnlyList<string> labels, int selected)
    {
        var result = selected;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Ui.Em(Ui.Space1, 0f));
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            var active = i == selected;
            bool pressed;
            if (active)
            {
                pressed = Theme.PrimaryButton(labels[i] + "##" + id + i);
            }
            else
            {
                pressed = ImGui.Button(labels[i] + "##" + id + i);
            }

            if (pressed)
            {
                result = i;
            }
        }

        return result;
    }
}
