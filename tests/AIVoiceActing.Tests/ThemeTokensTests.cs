namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class ThemeTokensTests
{
    private static readonly Rgb DarkWindow = Rgb.FromHex("#1A1A1E");
    private static readonly Rgb LightWindow = Rgb.FromHex("#F4F4F6");

    private static IEnumerable<(string Name, Rgb Colour)> TextTokens(ThemePalette p)
    {
        yield return ("Accent", p.Accent);
        yield return ("Ok", p.Ok);
        yield return ("Warn", p.Warn);
        yield return ("Error", p.Error);
        yield return ("Info", p.Info);
        yield return ("Muted", p.Muted);
    }

    [Fact]
    public void HexRoundTrip()
    {
        Assert.Equal("#4CC9C0", Rgb.FromHex("#4cc9c0").ToHex());
        Assert.Equal(new Rgb(0x0E, 0x1A, 0x1A), Rgb.FromHex("0E1A1A"));
    }

    [Fact]
    public void ContrastRatio_KnownValues()
    {
        Assert.Equal(21.0, ThemeTokens.ContrastRatio(Rgb.FromHex("#000000"), Rgb.FromHex("#FFFFFF")), 2);
        Assert.Equal(1.0, ThemeTokens.ContrastRatio(DarkWindow, DarkWindow), 3);
        Assert.Equal(
            ThemeTokens.ContrastRatio(DarkWindow, Rgb.FromHex("#26262C")),
            ThemeTokens.ContrastRatio(Rgb.FromHex("#26262C"), DarkWindow), 6);
    }

    [Fact]
    public void DarkPalette_MatchesSpec()
    {
        var p = ThemeTokens.Resolve(DarkWindow);
        Assert.False(p.IsLight);
        Assert.Equal("#4CC9C0", p.Accent.ToHex());
        Assert.Equal("#0E1A1A", p.OnAccent.ToHex());
        Assert.Equal("#6CCB7E", p.Ok.ToHex());
        Assert.Equal("#E8B84B", p.Warn.ToHex());
        Assert.Equal("#F0625F", p.Error.ToHex());
        Assert.Equal("#7FB2FF", p.Info.ToHex());
        Assert.Equal("#A3ADB5", p.Muted.ToHex());
    }

    [Theory]
    [InlineData("#1A1A1E")]
    [InlineData("#26262C")]
    public void DarkTextTokens_MeetAA(string background)
    {
        var bg = Rgb.FromHex(background);
        foreach (var (name, colour) in TextTokens(ThemeTokens.Resolve(DarkWindow)))
        {
            var ratio = ThemeTokens.ContrastRatio(colour, bg);
            Assert.True(ratio >= 4.5, $"{name} on {background} is {ratio:F2}:1");
        }
    }

    [Fact]
    public void OnAccent_ReadableOnAccentFill_InBothModes()
    {
        foreach (var bg in new[] { DarkWindow, LightWindow })
        {
            var p = ThemeTokens.Resolve(bg);
            Assert.True(ThemeTokens.ContrastRatio(p.OnAccent, p.Accent) >= 4.5);
        }
    }

    [Fact]
    public void LightBackground_FlipsToDarkerVariants()
    {
        var p = ThemeTokens.Resolve(LightWindow);
        Assert.True(p.IsLight);
        var dark = ThemeTokens.Resolve(DarkWindow);
        Assert.True(Rgb.Luminance(p.Accent) < Rgb.Luminance(dark.Accent));
        Assert.True(Rgb.Luminance(p.Error) < Rgb.Luminance(dark.Error));
    }

    [Theory]
    [InlineData("#F4F4F6")]
    [InlineData("#FFFFFF")]
    public void LightTextTokens_MeetAA(string background)
    {
        var bg = Rgb.FromHex(background);
        foreach (var (name, colour) in TextTokens(ThemeTokens.Resolve(LightWindow)))
        {
            var ratio = ThemeTokens.ContrastRatio(colour, bg);
            Assert.True(ratio >= 4.5, $"{name} on {background} is {ratio:F2}:1");
        }
    }

    [Fact]
    public void IsLight_UsesLuminanceThreshold()
    {
        Assert.False(ThemeTokens.IsLightBackground(Rgb.FromHex("#000000")));
        Assert.False(ThemeTokens.IsLightBackground(Rgb.FromHex("#808080")));
        Assert.True(ThemeTokens.IsLightBackground(Rgb.FromHex("#E0E0E0")));
    }

    [Fact]
    public void CardColour_LiftsAwayFromBackground()
    {
        Assert.True(Rgb.Luminance(ThemeTokens.CardFor(DarkWindow)) > Rgb.Luminance(DarkWindow));
        Assert.True(Rgb.Luminance(ThemeTokens.CardFor(LightWindow)) < Rgb.Luminance(LightWindow));
    }

    [Fact]
    public void PaletteText_StaysReadableOnDerivedCard()
    {
        var card = ThemeTokens.CardFor(DarkWindow);
        foreach (var (name, colour) in TextTokens(ThemeTokens.Resolve(DarkWindow)))
        {
            Assert.True(ThemeTokens.ContrastRatio(colour, card) >= 4.5, name);
        }
    }
}
