namespace AIVoiceActing.UI.Components;

using AIVoiceActing.UI.State;
using Dalamud.Interface;

/// <summary>
/// The one-click preview button of a voice. Idle it plays; while the voice is being prepared it
/// shows a spinner (click to cancel); while it speaks it shows stop. Only one preview runs at a
/// time: starting another voice cancels this one, so a row never has to guess whose turn it is.
/// </summary>
public static class PreviewButton
{
    /// <summary>True while the preview controller is preparing or playing the voice with this key.</summary>
    public static bool IsActive(PreviewController preview, string key) =>
        preview.Key == key && preview.State is PreviewState.Synthesizing or PreviewState.Playing;

    /// <summary>
    /// Draws the button. <paramref name="blockedReason"/> (null when previews work) is the tooltip of
    /// the disabled button; callers also show it as visible text once per list.
    /// </summary>
    public static void Draw(string id, PreviewController preview, string key, string? blockedReason, Action start)
    {
        var mine = preview.Key == key;
        if (mine && preview.State == PreviewState.Synthesizing)
        {
            if (IconButton.Draw(id, FontAwesomeIcon.Spinner, "Preparing the voice. Click to cancel."))
            {
                preview.Cancel();
            }
        }
        else if (mine && preview.State == PreviewState.Playing)
        {
            if (IconButton.Draw(id, FontAwesomeIcon.Stop, "Stop"))
            {
                preview.Cancel();
            }
        }
        else if (IconButton.Draw(id, FontAwesomeIcon.Play, "Hear this voice", blockedReason is null, blockedReason))
        {
            start();
        }
    }
}
