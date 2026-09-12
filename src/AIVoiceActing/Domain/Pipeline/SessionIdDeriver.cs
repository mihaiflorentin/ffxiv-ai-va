namespace AIVoiceActing.Domain.Pipeline;

/// <summary>
/// Derives the dialogue-context session id for a captured line (pure): a cutscene in
/// progress owns one shared "cutscene" window (the whole cast shares context); a visible
/// Talk addon scopes a window to the speaking character; every other surface gets none and
/// feeds no context window.
/// </summary>
public static class SessionIdDeriver
{
    public const string CutsceneSessionId = "cutscene";

    /// <param name="cutsceneActive">A cutscene condition is active.</param>
    /// <param name="talkVisible">The Talk addon is visible.</param>
    /// <param name="speakerKey">Resolved speaker key (stabilizes the talk session id).</param>
    public static string? Derive(bool cutsceneActive, bool talkVisible, string speakerKey)
    {
        if (cutsceneActive)
        {
            return CutsceneSessionId;
        }

        return talkVisible ? $"talk:{speakerKey}" : null;
    }
}
