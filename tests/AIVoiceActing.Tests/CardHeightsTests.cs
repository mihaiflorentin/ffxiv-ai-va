namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class CardHeightsTests
{
    [Fact]
    public void UnknownCard_UsesTheFallback()
    {
        Assert.Equal(3f, new CardHeights().Get("quality", 3f));
    }

    [Fact]
    public void DrawnCard_KeepsItsMeasuredHeight()
    {
        var heights = new CardHeights();

        heights.Record("quality", 240f, drew: true);

        Assert.Equal(240f, heights.Get("quality", 3f));
    }

    [Fact]
    public void ClippedCard_KeepsItsLastRealHeight()
    {
        var heights = new CardHeights();
        heights.Record("quality", 240f, drew: true);

        // Scrolled out of view: ImGui skipped the body, so only the padding was measured.
        heights.Record("quality", 12f, drew: false);

        Assert.Equal(240f, heights.Get("quality", 3f));
    }
}
