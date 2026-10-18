namespace AIVoiceActing.UI.Components;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>An empty list is an invitation: one sentence about why, plus the action that fixes it.</summary>
public static class EmptyState
{
    /// <summary>Returns true when the action button is clicked (pass a null label for no action).</summary>
    public static bool Draw(FontAwesomeIcon icon, string title, string hint, string? actionLabel = null)
    {
        Ui.Gap(Ui.Space3);
        Theme.IconText(icon, Theme.Muted);
        ImGui.SameLine(0f, Ui.Em(Ui.Space2));
        ImGui.TextUnformatted(title);
        Ui.MutedText(hint);
        var clicked = false;
        if (actionLabel is not null)
        {
            Ui.Gap(Ui.Space1);
            clicked = Theme.PrimaryButton(actionLabel);
        }

        Ui.Gap(Ui.Space3);
        return clicked;
    }
}
