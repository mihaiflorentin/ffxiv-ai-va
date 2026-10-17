namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

/// <summary>Joined buttons for a short either-or choice (for example Fast / Balanced / Most expressive).</summary>
public static class SegmentedControl
{
    /// <summary>Returns the selected index (the clicked one, or <paramref name="selected"/> unchanged).</summary>
    public static int Draw(string id, IReadOnlyList<string> labels, int selected) =>
        Draw(id, labels, selected, null);

    /// <summary>
    /// As above, with a reason per segment: a non-null entry greys that segment out and shows the
    /// reason as its tooltip. Pair an important reason with visible text beside the control too.
    /// An index of -1 selects nothing (a setup that matches none of the choices).
    /// </summary>
    public static int Draw(string id, IReadOnlyList<string> labels, int selected, IReadOnlyList<string?>? disabledReasons)
    {
        var result = selected;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Ui.Em(Ui.Space1, 0f));
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            var reason = disabledReasons is not null && i < disabledReasons.Count ? disabledReasons[i] : null;
            var active = i == selected;
            if (reason is not null)
            {
                ImGui.BeginDisabled();
            }

            var pressed = active
                ? Theme.PrimaryButton(labels[i] + "##" + id + i)
                : ImGui.Button(labels[i] + "##" + id + i);
            if (reason is not null)
            {
                ImGui.EndDisabled();
                Ui.Tooltip(reason);
            }
            else if (pressed)
            {
                result = i;
            }
        }

        return result;
    }
}
