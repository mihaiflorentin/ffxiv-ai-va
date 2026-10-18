namespace AIVoiceActing.UI;

using AIVoiceActing.UI.State;

/// <summary>
/// Where transient confirmations go (a corner toast outside the settings window). Pages and
/// models see this interface only; the plugin root supplies the Dalamud-backed implementation.
/// </summary>
public interface IToastSink
{
    void Show(Severity severity, string title, string? message = null);
}
