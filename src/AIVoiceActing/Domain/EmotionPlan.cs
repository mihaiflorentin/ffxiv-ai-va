namespace AIVoiceActing.Domain;

/// <summary>
/// Delivery direction for one line: how the synthesizer should perform the text.
/// <see cref="Exaggeration"/> is 0..1 (0 = flat, 1 = theatrical); <see cref="Volume"/> is 0..1.
/// </summary>
public sealed record EmotionPlan(
    float Exaggeration,
    IReadOnlyList<string> Tags,
    int PauseBeforeMs,
    float Volume)
{
    public static EmotionPlan Neutral { get; } = new(Exaggeration: 0.5f, Tags: [], PauseBeforeMs: 0, Volume: 1f);
}
