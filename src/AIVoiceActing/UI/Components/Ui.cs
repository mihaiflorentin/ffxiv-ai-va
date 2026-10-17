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
    public static void Gap(float spaceEm = Space2) => ImGui.Dummy(new Vector2(0f, Em(spaceEm)));

    /// <summary>Width left in the current content region, for controls that fill the row.</summary>
    public static float Fill() => ImGui.GetContentRegionAvail().X;

    /// <summary>Wrapped secondary text in the muted token colour.</summary>
    public static void MutedText(string text)
    {
        using var colour = Theme.PushText(Theme.Muted);
        ImGui.TextWrapped(text);
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
