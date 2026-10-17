namespace AIVoiceActing.UI.Components;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>Status as glyph + word + colour, so no meaning rides on colour alone.</summary>
public static class StatusPill
{
    public static void Draw(Severity severity, string text)
    {
        var colour = Theme.Colour(severity);
        Theme.IconText(Theme.Icon(severity), colour);
        ImGui.SameLine(0f, Ui.Em(Ui.Space2));
        using var c = Theme.PushText(colour);
        ImGui.TextUnformatted(text);
    }
}
