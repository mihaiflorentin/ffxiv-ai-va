namespace AIVoiceActing.Domain;

using System.Text.RegularExpressions;

/// <summary>
/// Table-driven rules director (zero footprint): maps a line's surface signals to an
/// <see cref="EmotionPlan"/>. FIXED priority — the first matching rule wins, strongest
/// explicit signal first (documented order, do not reorder casually):
/// <list type="number">
/// <item><b>Angry</b> — any ALL-CAPS word of 2+ letters: shouting overrides every punctuation signal → 0.8</item>
/// <item><b>Sad</b> — ellipsis ("..."/"…") or the keyword "sigh": trailing hesitation colors the whole line → 0.3 + 250 ms pause; a literal sigh also emits the [sigh] tag</item>
/// <item><b>Amused</b> — laugh token (hahaha/hehe/lol): a paralinguistic event beats punctuation → 0.6 + [laughs]</item>
/// <item><b>Excited</b> — "!" → 0.75</item>
/// <item><b>Curious</b> — "?" → 0.6</item>
/// <item><b>Neutral</b> — default → 0.5</item>
/// </list>
/// </summary>
public static class EmotionRules
{
    private static readonly Regex CapsWord = new(@"\b[A-Z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex LaughToken = new(@"\b(?:(?:ha){2,}|hehe|lol)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SighKeyword = new(@"\bsigh", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static EmotionPlan Plan(string? line)
    {
        var text = line ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return EmotionPlan.Neutral;
        }

        // 1. Angry — ALL-CAPS shouting beats everything else.
        if (CapsWord.IsMatch(text))
        {
            return Simple("angry", 0.8f);
        }

        // 2. Sad — ellipsis or a sigh; the pause does the "slow" work, the tag only for a literal sigh.
        if (ContainsEllipsis(text) || SighKeyword.IsMatch(text))
        {
            IReadOnlyList<string> tags = SighKeyword.IsMatch(text) ? ["sigh"] : [];
            return new EmotionPlan("sad", 0.3f, tags, PauseBeforeMs: 250, Volume: 1f);
        }

        // 3. Amused — laughter is a paralinguistic event, stronger than trailing punctuation.
        if (LaughToken.IsMatch(text))
        {
            return Simple("amused", 0.6f, ["laughs"]);
        }

        // 4. Excited.
        if (text.Contains('!'))
        {
            return Simple("excited", 0.75f);
        }

        // 5. Curious.
        if (text.Contains('?'))
        {
            return Simple("curious", 0.6f);
        }

        // 6. Neutral.
        return EmotionPlan.Neutral;
    }

    private static bool ContainsEllipsis(string text) =>
        text.Contains("...", StringComparison.Ordinal) || text.Contains('…');

    private static EmotionPlan Simple(string emotion, float exaggeration, IReadOnlyList<string>? tags = null) =>
        new(emotion, exaggeration, tags ?? [], PauseBeforeMs: 0, Volume: 1f);
}
