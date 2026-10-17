namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Domain;

/// <summary>
/// How the chunked Turbo decode is shaped. <see cref="PromptTokens"/> is how many tokens (and twice as many
/// mel frames) of the reference clip each decoder call keeps: the decoder re-reads its whole prompt on every
/// call, so a shorter prompt is the biggest lever on the cost of a small chunk. The full prompt of the
/// bundled voice is 186 tokens (7.4 s). Speaker likeness of the output (cosine to the reference, ten lines,
/// docs/plans/2026-10-gpu-spike-results.md): 0.914 with all 186, 0.913 with 120, 0.912 with 100, 0.900 with
/// 80, 0.905 with 60, 0.887 with 40; the word error rate does not move. 100 keeps the likeness of the full
/// prompt and makes a first window about 1.6 times cheaper. <see cref="HalfFadeSamples"/> is half of the
/// cross-fade between two chunks: 960 samples (an 80 ms fade) with 12 tokens of context before and 6 after.
/// In the seam checks on ten lines every fade length kept the word error rate; this one kept the sharpest
/// edge near a seam at the level of the undisturbed decode (docs/plans/2026-10-gpu-spike-results.md).
/// </summary>
public sealed record StreamingOptions(ChunkScheduleOptions Chunks, int PromptTokens, int HalfFadeSamples)
{
    public static StreamingOptions Default { get; } = new(ChunkScheduleOptions.Default, 100, 960);
}

/// <summary>What one streamed line cost, in milliseconds.</summary>
/// <param name="FirstAudioMs">From the start of the line (after the reference clip was ready) to the first samples handed out.</param>
/// <param name="TotalMs">Same clock, to the last samples.</param>
/// <param name="Chunks">Decoder calls made.</param>
/// <param name="SpeechTokens">Speech tokens the language model produced.</param>
/// <param name="LanguageModelMs">Time the language model loop ran (on its own thread, overlapping the decoder).</param>
/// <param name="DecoderMs">Time spent inside decoder calls.</param>
/// <param name="LengthMismatches">Decoder windows whose length differed from the plan (should be 0).</param>
public sealed record StreamingStats(
    long FirstAudioMs,
    long TotalMs,
    int Chunks,
    int SpeechTokens,
    long LanguageModelMs,
    long DecoderMs,
    int LengthMismatches);
