namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Maps an emotion plan (plus the resolved reference voice) onto the synthesizer port:
/// exaggeration is the plan value plus the per-speaker bias, clamped to 0..1; tags are
/// carried on the request — the synthesizer renders them as "[tag] " prompt prefixes.
/// </summary>
public static class SpeechRequestMapper
{
    public static SynthesisRequest ToRequest(
        EmotionPlan plan,
        string referenceVoiceId,
        string text,
        float exaggerationBias = 0f) =>
        new(
            ReferenceVoiceId: referenceVoiceId,
            Text: text,
            Exaggeration: Math.Clamp(plan.Exaggeration + exaggerationBias, 0f, 1f),
            Tags: plan.Tags);
}
