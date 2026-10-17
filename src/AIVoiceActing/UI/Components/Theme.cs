namespace AIVoiceActing.UI.Components;

using System.Numerics;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

/// <summary>
/// Resolves the semantic colour tokens against the user's own Dalamud theme (window background
/// luminance picks the dark or light variants) and exposes scoped push helpers. Nothing global
/// is overwritten: every colour is pushed and popped by the caller's <c>using</c>.
/// </summary>
public static class Theme
{
    private static Rgb cachedBackground = default;
    private static ThemePalette? cachedPalette;

    public static ThemePalette Palette
    {
        get
        {
            var bg = ToRgb(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg]);
            if (cachedPalette is null || bg != cachedBackground)
            {
                cachedBackground = bg;
                cachedPalette = ThemeTokens.Resolve(bg);
            }

            return cachedPalette;
        }
    }

    public static Vector4 Accent => ToV4(Palette.Accent);

    public static Vector4 OnAccent => ToV4(Palette.OnAccent);

    public static Vector4 Ok => ToV4(Palette.Ok);

    public static Vector4 Warn => ToV4(Palette.Warn);

    public static Vector4 Error => ToV4(Palette.Error);

    public static Vector4 Info => ToV4(Palette.Info);

    public static Vector4 Muted => ToV4(Palette.Muted);

    /// <summary>Card fill derived from the window background.</summary>
    public static Vector4 Card => ToV4(ThemeTokens.CardFor(ToRgb(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg])));

    public static Vector4 Colour(Severity severity) => severity switch
    {
        Severity.Success => Ok,
        Severity.Warning => Warn,
        Severity.Error => Error,
        _ => Info,
    };

    /// <summary>Status is never colour-only: each severity has its own glyph.</summary>
    public static FontAwesomeIcon Icon(Severity severity) => severity switch
    {
        Severity.Success => FontAwesomeIcon.CheckCircle,
        Severity.Warning => FontAwesomeIcon.ExclamationTriangle,
        Severity.Error => FontAwesomeIcon.TimesCircle,
        _ => FontAwesomeIcon.InfoCircle,
    };

    public static Vector4 WithAlpha(Vector4 c, float alpha) => new(c.X, c.Y, c.Z, alpha);

    public static uint ToU32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    public static ImRaii.ColorDisposable PushText(Vector4 colour) => ImRaii.PushColor(ImGuiCol.Text, colour);

    /// <summary>Filled accent button style (teal fill, dark text). Use for the one primary action per area.</summary>
    public static ImRaii.ColorDisposable PushPrimary() => ImRaii.PushColor(ImGuiCol.Button, Accent)
        .Push(ImGuiCol.ButtonHovered, Shade(Accent, 0.12f))
        .Push(ImGuiCol.ButtonActive, Shade(Accent, -0.12f))
        .Push(ImGuiCol.Text, OnAccent);

    /// <summary>Destructive button style: darker red fill with white text (5.7:1).</summary>
    public static ImRaii.ColorDisposable PushDanger() => ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.70f, 0.25f, 0.24f, 1f))
        .Push(ImGuiCol.ButtonHovered, new Vector4(0.78f, 0.30f, 0.28f, 1f))
        .Push(ImGuiCol.ButtonActive, new Vector4(0.60f, 0.20f, 0.19f, 1f))
        .Push(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));

    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        using var style = PushPrimary();
        return ImGui.Button(label, size);
    }

    public static bool DangerButton(string label, Vector2 size = default)
    {
        using var style = PushDanger();
        return ImGui.Button(label, size);
    }

    /// <summary>Draws <paramref name="icon"/> in the icon font at the cursor, in <paramref name="colour"/>.</summary>
    public static void IconText(FontAwesomeIcon icon, Vector4 colour)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        using var c = PushText(colour);
        ImGui.TextUnformatted(icon.ToIconString());
    }

    private static Vector4 Shade(Vector4 c, float amount) => new(
        Math.Clamp(c.X + amount, 0f, 1f),
        Math.Clamp(c.Y + amount, 0f, 1f),
        Math.Clamp(c.Z + amount, 0f, 1f),
        c.W);

    private static Rgb ToRgb(Vector4 c) => new(
        (byte)Math.Round(Math.Clamp(c.X, 0f, 1f) * 255f),
        (byte)Math.Round(Math.Clamp(c.Y, 0f, 1f) * 255f),
        (byte)Math.Round(Math.Clamp(c.Z, 0f, 1f) * 255f));

    private static Vector4 ToV4(Rgb c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
}
