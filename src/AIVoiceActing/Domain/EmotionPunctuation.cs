namespace AIVoiceActing.Domain;

/// <summary>
/// Shapes terminal punctuation from an emotion plan's intensity: Kokoro's prosody
/// surface is punctuation plus voice choice, so the plan's exaggeration becomes
/// delivery. ≥0.75 (energetic) swaps a terminal '.' for '!'; ≤0.25 (soft/flat) swaps
/// it for '…' (a trailing falling beat). Text already ending in '?', '!', or '…',
/// or without terminal punctuation, is never touched.
/// </summary>
public static class EmotionPunctuation
{
    public static string Shape(string text, float exaggeration)
    {
        if (string.IsNullOrEmpty(text) || text[^1] != '.')
        {
            return text;
        }

        return Math.Clamp(exaggeration, 0f, 1f) switch
        {
            >= 0.75f => text[..^1] + "!",
            <= 0.25f => text[..^1] + "…",
            _ => text,
        };
    }
}
