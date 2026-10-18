namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;

/// <summary>
/// A checkbox with its explanation in muted text underneath, lined up with the label. The help
/// is visible text, never hover-only. Sub-options pass <c>indent: true</c> to sit under their parent.
/// </summary>
public static class CheckRow
{
    public static void Draw(string label, string? help, Func<bool> get, Action<bool> apply, bool indent = false)
    {
        var offset = Offset();
        if (indent)
        {
            ImGui.Indent(offset);
        }

        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            apply(value);
        }

        if (!string.IsNullOrEmpty(help))
        {
            ImGui.Indent(offset);
            Ui.MutedText(help);
            ImGui.Unindent(offset);
        }

        if (indent)
        {
            ImGui.Unindent(offset);
        }
    }

    /// <summary>Width of a checkbox box plus its gap to the label: where the label text starts.</summary>
    public static float Offset() => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
}
