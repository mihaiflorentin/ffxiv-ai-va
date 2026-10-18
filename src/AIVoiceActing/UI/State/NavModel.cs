namespace AIVoiceActing.UI.State;

/// <summary>The four top-level tabs of the settings window, in display order.</summary>
public enum NavTab
{
    Home,
    Voices,
    Reading,
    Settings,
}

/// <summary>Anchor keys the What's read page understands (see <see cref="NavModel.OpenAt"/>).</summary>
public static class ReadingAnchors
{
    /// <summary>"More options", scrolled to the style-tags section (the styles command lands here).</summary>
    public const string Styles = "styles";
}

/// <summary>
/// Which tab is showing. Clicking a tab calls <see cref="Select"/>; deep links (a command, the
/// now-speaking gear, a banner action) call <see cref="OpenAt"/>, which also asks the tab bar to
/// force the tab for one frame and leaves an anchor the page can consume once to open the
/// right section. The selection is persisted through the callback (config <c>LastPage</c>)
/// whenever it changes.
/// </summary>
public sealed class NavModel
{
    private readonly Action<string> persist;
    private NavTab? forced;
    private NavTab anchorTab;
    private string? anchor;

    public NavModel(string? lastPage, Action<string> persist)
    {
        this.persist = persist;
        this.Selected = Enum.TryParse<NavTab>(lastPage, ignoreCase: true, out var tab) && Enum.IsDefined(tab)
            ? tab
            : NavTab.Home;
        this.forced = this.Selected; // restore the persisted tab on the first frame
    }

    public NavTab Selected { get; private set; }

    /// <summary>Tab-bar caption for a tab.</summary>
    public static string Label(NavTab tab) => tab switch
    {
        NavTab.Home => "Home",
        NavTab.Voices => "Voices",
        NavTab.Reading => "What's read",
        _ => "Settings",
    };

    /// <summary>A user click on a tab.</summary>
    public void Select(NavTab tab)
    {
        if (tab == this.Selected)
        {
            return;
        }

        this.Selected = tab;
        this.persist(tab.ToString());
    }

    /// <summary>Programmatic navigation: select <paramref name="tab"/> and remember <paramref name="anchor"/> for its page.</summary>
    public void OpenAt(NavTab tab, string? anchor = null)
    {
        this.Select(tab);
        this.forced = tab;
        this.anchorTab = tab;
        this.anchor = anchor;
    }

    /// <summary>Deep link used by the styles command: What's read, with More options open at the style tags.</summary>
    public void OpenStyleTags() => this.OpenAt(NavTab.Reading, ReadingAnchors.Styles);

    /// <summary>The tab the tab bar must force this frame, once; null when nothing is pending.</summary>
    public NavTab? TakeForcedTab()
    {
        var tab = this.forced;
        this.forced = null;
        return tab;
    }

    /// <summary>The pending anchor for <paramref name="tab"/>, once; other tabs see null and leave it in place.</summary>
    public string? ConsumeAnchor(NavTab tab)
    {
        if (this.anchor is null || tab != this.anchorTab)
        {
            return null;
        }

        var result = this.anchor;
        this.anchor = null;
        return result;
    }
}
