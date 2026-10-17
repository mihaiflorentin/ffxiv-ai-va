namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;

/// <summary>Icon-only button. A tooltip is mandatory, so an icon never stands alone without a name.</summary>
public static class IconButton
{
    /// <summary>
    /// Draws the button. When <paramref name="enabled"/> is false it is greyed out and the tooltip
    /// carries <paramref name="disabledReason"/> (falling back to the normal tooltip).
    /// </summary>
    public static bool Draw(string id, FontAwesomeIcon icon, string tooltip, bool enabled = true, string? disabledReason = null)
    {
        var clicked = false;
        ImGui.BeginDisabled(!enabled);
        if (ImGuiComponents.IconButton(id, icon))
        {
            clicked = enabled;
        }

        ImGui.EndDisabled();
        Ui.Tooltip(!enabled && disabledReason is not null ? disabledReason : tooltip);
        return clicked;
    }
}
