namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;

/// <summary>
/// Buttons that can be switched off and still say why: a disabled button keeps its look and
/// carries the reason as a tooltip, so a greyed-out control is never a dead end. Pair the
/// important reasons with visible text next to the button as well; a tooltip alone needs a hover.
/// </summary>
public static class ActionButton
{
    /// <summary>The one filled accent button of an area.</summary>
    public static bool Primary(string label, bool enabled = true, string? disabledReason = null)
    {
        if (enabled)
        {
            return Theme.PrimaryButton(label);
        }

        ImGui.BeginDisabled();
        Theme.PrimaryButton(label);
        ImGui.EndDisabled();
        Reason(disabledReason);
        return false;
    }

    /// <summary>A plain button.</summary>
    public static bool Secondary(string label, bool enabled = true, string? disabledReason = null)
    {
        if (enabled)
        {
            return ImGui.Button(label);
        }

        ImGui.BeginDisabled();
        ImGui.Button(label);
        ImGui.EndDisabled();
        Reason(disabledReason);
        return false;
    }

    private static void Reason(string? disabledReason)
    {
        if (disabledReason is not null)
        {
            Ui.Tooltip(disabledReason);
        }
    }
}
