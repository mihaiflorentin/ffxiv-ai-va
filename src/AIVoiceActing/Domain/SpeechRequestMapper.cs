namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Maps an emotion plan (plus the resolved reference voice) onto the synthesizer port:
/// exaggeration clamped to 0..1 (plan values plus per-slot bias stay in contract range),
/// tags carried on the request — the synthesizer renders them as "[tag] " prompt prefixes.
/// </summary>
public static class SpeechRequestMapper
{
    public static SynthesisRequest ToRequest(EmotionPlan plan, string referenceVoiceId, string text) =>
        new(
            ReferenceVoiceId: referenceVoiceId,
            Text: text,
            Exaggeration: Math.Clamp(plan.Exaggeration, 0f, 1f),
            Tags: plan.Tags);
}
