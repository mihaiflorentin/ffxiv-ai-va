namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.State;

/// <summary>One tab of the settings window. The shell draws the page inside a scrolling child above the footer.</summary>
public interface IPage
{
    NavTab Tab { get; }

    /// <summary>Draws the page body; the window, tab bar, footer and modals belong to the shell.</summary>
    void Draw();
}
