namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>Single-line search with a clear button that appears once there is text.</summary>
public static class SearchBox
{
    public const float DefaultWidthEm = 16f;

    /// <summary>Returns true when the text changed (typing or clearing).</summary>
    public static bool Draw(string id, ref string text, string hint = "Search", float widthEm = DefaultWidthEm, int maxLength = 64)
    {
        var changed = false;
        ImGui.SetNextItemWidth(Ui.Em(widthEm));
        if (ImGui.InputTextWithHint("##search-" + id, hint, ref text, maxLength))
        {
            changed = true;
        }

        if (text.Length > 0)
        {
            ImGui.SameLine();
            if (IconButton.Draw("##clear-" + id, FontAwesomeIcon.Times, "Clear search"))
            {
                text = string.Empty;
                changed = true;
            }
        }

        return changed;
    }
}
