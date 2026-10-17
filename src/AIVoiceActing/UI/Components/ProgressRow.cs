namespace AIVoiceActing.UI.Components;

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

/// <summary>Labelled progress bar; indeterminate when the total is unknown.</summary>
public static class ProgressRow
{
    /// <summary>
    /// <paramref name="fraction"/> in 0..1, or null for "working, size unknown". Under
    /// <paramref name="reduceMotion"/> the indeterminate bar is drawn static (empty) with the label carrying the state.
    /// </summary>
    public static void Draw(string label, float? fraction, string? detail = null, bool reduceMotion = false)
    {
        ImGui.TextUnformatted(label);
        if (!string.IsNullOrEmpty(detail))
        {
            ImGui.SameLine();
            Ui.MutedText(detail);
        }

        var size = new Vector2(Ui.Fill(), Ui.Em(0.6f));
        using var fill = ImRaii.PushColor(ImGuiCol.PlotHistogram, Theme.Accent);
        if (fraction is { } f)
        {
            ImGui.ProgressBar(Math.Clamp(f, 0f, 1f), size, string.Empty);
        }
        else if (reduceMotion)
        {
            ImGui.ProgressBar(0f, size, string.Empty);
        }
        else
        {
            // A negative fraction makes ImGui draw the sliding indeterminate bar.
            ImGui.ProgressBar(-1f * (float)ImGui.GetTime(), size, string.Empty);
        }
    }
}
