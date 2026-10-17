namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class NavModelTests
{
    [Theory]
    [InlineData("", NavTab.Home)]
    [InlineData(null, NavTab.Home)]
    [InlineData("nonsense", NavTab.Home)]
    [InlineData("Voices", NavTab.Voices)]
    [InlineData("Reading", NavTab.Reading)]
    [InlineData("Settings", NavTab.Settings)]
    public void Restores_LastPage_OrFallsBackToHome(string? last, NavTab expected)
    {
        var nav = new NavModel(last, _ => { });
        Assert.Equal(expected, nav.Selected);
    }

    [Fact]
    public void Select_ChangesTab_AndPersistsOnlyOnChange()
    {
        var saved = new List<string>();
        var nav = new NavModel("", saved.Add);

        nav.Select(NavTab.Voices);
        nav.Select(NavTab.Voices);
        nav.Select(NavTab.Settings);

        Assert.Equal(NavTab.Settings, nav.Selected);
        Assert.Equal(["Voices", "Settings"], saved);
    }

    [Fact]
    public void OpenAt_SelectsTab_ForcesItOnce_AndKeepsAnchorUntilConsumed()
    {
        var nav = new NavModel("", _ => { });
        nav.TakeForcedTab(); // initial restore frame

        nav.OpenAt(NavTab.Settings, "keybinds");

        Assert.Equal(NavTab.Settings, nav.Selected);
        Assert.Equal(NavTab.Settings, nav.TakeForcedTab());
        Assert.Null(nav.TakeForcedTab());

        Assert.Null(nav.ConsumeAnchor(NavTab.Voices));
        Assert.Equal("keybinds", nav.ConsumeAnchor(NavTab.Settings));
        Assert.Null(nav.ConsumeAnchor(NavTab.Settings));
    }

    [Fact]
    public void FirstFrame_ForcesTheRestoredTab()
    {
        var nav = new NavModel("Reading", _ => { });
        Assert.Equal(NavTab.Reading, nav.TakeForcedTab());
        Assert.Null(nav.TakeForcedTab());
    }

    [Fact]
    public void Select_ByUserClick_DoesNotForce()
    {
        var nav = new NavModel("", _ => { });
        nav.TakeForcedTab();
        nav.Select(NavTab.Voices);
        Assert.Null(nav.TakeForcedTab());
    }

    [Fact]
    public void Labels_AreTheFourTabs()
    {
        Assert.Equal(
            ["Home", "Voices", "What's read", "Settings"],
            Enum.GetValues<NavTab>().Select(NavModel.Label));
    }
}
