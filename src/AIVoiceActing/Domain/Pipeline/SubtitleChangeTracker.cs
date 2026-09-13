namespace AIVoiceActing.Domain.Pipeline;

/// <summary>
/// Adjacent-duplicate suppression for per-tick capture surfaces (cutscene subtitles):
/// emits only when (speaker, text) changes, so a subtitle that stays on screen for many
/// frames is spoken once, not once per tick. Blank frames are filtered by the caller and
/// therefore never reset the state. Pure core; the source stays hard-disabled pending
/// in-game verification.
/// </summary>
public sealed class SubtitleChangeTracker
{
    private (string Speaker, string Text) last;

    /// <summary>True when the line differs from the previously emitted one.</summary>
    public bool ShouldEmit(string speaker, string text)
    {
        var current = (speaker, text);
        if (current == this.last)
        {
            return false;
        }

        this.last = current;
        return true;
    }
}
