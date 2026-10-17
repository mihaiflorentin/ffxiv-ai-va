namespace AIVoiceActing.UI.State;

using System.Globalization;

/// <summary>An sRGB colour without alpha; pure so contrast maths is testable off-game.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        return new Rgb(
            byte.Parse(s[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public string ToHex() => $"#{this.R:X2}{this.G:X2}{this.B:X2}";

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Rgb c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    /// <summary>Linear blend towards <paramref name="target"/> by <paramref name="amount"/> (0..1).</summary>
    public Rgb Mix(Rgb target, double amount)
    {
        static byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp(Math.Round(a + ((b - a) * t)), 0, 255);
        return new Rgb(Lerp(this.R, target.R, amount), Lerp(this.G, target.G, amount), Lerp(this.B, target.B, amount));
    }
}

/// <summary>Semantic colours for one theme polarity.</summary>
public sealed record ThemePalette(
    bool IsLight,
    Rgb Accent,
    Rgb OnAccent,
    Rgb Ok,
    Rgb Warn,
    Rgb Error,
    Rgb Info,
    Rgb Muted);

/// <summary>
/// Pure colour-token resolver. The dark set is tuned for Dalamud's darkest window backgrounds
/// (#1A1A1E and cards at #26262C, both at 4.5:1 or better); light themes get darker variants
/// of the same hues, chosen by the luminance of the window background.
/// </summary>
public static class ThemeTokens
{
    /// <summary>Backgrounds with relative luminance above this count as a light theme.</summary>
    public const double LightThreshold = 0.4;

    public static readonly ThemePalette Dark = new(
        IsLight: false,
        Accent: Rgb.FromHex("#4CC9C0"),
        OnAccent: Rgb.FromHex("#0E1A1A"),
        Ok: Rgb.FromHex("#6CCB7E"),
        Warn: Rgb.FromHex("#E8B84B"),
        Error: Rgb.FromHex("#F0625F"),
        Info: Rgb.FromHex("#7FB2FF"),
        Muted: Rgb.FromHex("#A3ADB5"));

    public static readonly ThemePalette Light = new(
        IsLight: true,
        Accent: Rgb.FromHex("#0B6E68"),
        OnAccent: Rgb.FromHex("#FFFFFF"),
        Ok: Rgb.FromHex("#1B6E32"),
        Warn: Rgb.FromHex("#7A5200"),
        Error: Rgb.FromHex("#B3261E"),
        Info: Rgb.FromHex("#1A55B0"),
        Muted: Rgb.FromHex("#545E66"));

    public static bool IsLightBackground(Rgb windowBackground) => Rgb.Luminance(windowBackground) > LightThreshold;

    public static ThemePalette Resolve(Rgb windowBackground) => IsLightBackground(windowBackground) ? Light : Dark;

    /// <summary>Card fill: the window background nudged about 6% away from it (lighter on dark, darker on light).</summary>
    public static Rgb CardFor(Rgb windowBackground) => IsLightBackground(windowBackground)
        ? windowBackground.Mix(new Rgb(0, 0, 0), 0.04)
        : windowBackground.Mix(new Rgb(255, 255, 255), 0.06);

    /// <summary>WCAG contrast ratio between two colours, 1 to 21, symmetric.</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var la = Rgb.Luminance(a);
        var lb = Rgb.Luminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }
}
