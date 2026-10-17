namespace AIVoiceActing.UI.State;

/// <summary>Wording for the Now Speaking panel.</summary>
public static class NowSpeakingModel
{
    /// <summary>The mood as a player reads it: "neutral" when the line has none, otherwise the detected emotion in lower case.</summary>
    public static string Mood(string? emotion) =>
        string.IsNullOrWhiteSpace(emotion) ? "neutral" : emotion.Trim().ToLowerInvariant();
}
