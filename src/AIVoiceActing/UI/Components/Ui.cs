namespace AIVoiceActing.UI.Components;

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

/// <summary>
/// Size helpers. Everything is measured in em (the current font size, which already includes
/// GlobalScale) so layouts hold from 100% to 200% scale. This file and Theme.cs are the only
/// UI sources allowed to contain raw numeric sizes (see UiHygieneTests).
/// </summary>
public static class Ui
{
    public const float Space1 = 0.25f;
    public const float Space2 = 0.5f;
    public const float Space3 = 1.0f;
    public const float Space4 = 1.5f;
    public const float Space5 = 2.0f;

    /// <summary>Pivot that centres a window on its anchor point.</summary>
    public static readonly Vector2 CenterPivot = new(0.5f, 0.5f);

    /// <summary><paramref name="k"/> times the current font size.</summary>
    public static float Em(float k) => ImGui.GetFontSize() * k;

    public static Vector2 Em(float x, float y) => new(Em(x), Em(y));

    /// <summary>Pixels multiplied by Dalamud's global scale; for one-pixel rules and bitmap sizes only.</summary>
    public static float Scaled(float px) => px * ImGuiHelpers.GlobalScale;

    /// <summary>Row height used for list rows: a frame plus a little air.</summary>
    public static float RowHeight() => ImGui.GetFrameHeight() + Em(Space1);

    /// <summary>Vertical gap of <paramref name="spaceEm"/> em.</summary>
    public static void Gap(float spaceEm = Space2) => ImGui.Dummy(Vector2.UnitY * Em(spaceEm));

    /// <summary>Width left in the current content region, for controls that fill the row.</summary>
    public static float Fill() => ImGui.GetContentRegionAvail().X;

    /// <summary>Wrapped secondary text in the muted token colour.</summary>
    public static void MutedText(string text)
    {
        using var colour = Theme.PushText(Theme.Muted);
        ImGui.TextWrapped(text);
    }

    /// <summary>Muted text wrapped at a fixed width, for auto-sizing popups and modals that have no width to wrap to yet.</summary>
    public static void MutedTextAt(string text, float widthEm)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Em(widthEm));
        MutedText(text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>One line of muted text that does not wrap (labels and counts that sit beside controls).</summary>
    public static void MutedTextInline(string text)
    {
        using var colour = Theme.PushText(Theme.Muted);
        ImGui.TextUnformatted(text);
    }

    /// <summary>
    /// <paramref name="text"/> cut with an ellipsis so it fits in <paramref name="maxWidth"/> pixels
    /// (for fixed-height list rows; the full text must be shown somewhere else as well).
    /// </summary>
    public static string Ellipsize(string text, float maxWidth)
    {
        if (maxWidth <= 0f || ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }

        const string Dots = "…";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + Dots).X <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return text[..low].TrimEnd() + Dots;
    }

    /// <summary>Shows <paramref name="text"/> as a tooltip for the previous item (also when it is disabled).</summary>
    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(text);
        }
    }
}
