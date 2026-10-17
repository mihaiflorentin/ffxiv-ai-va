namespace AIVoiceActing.Infrastructure.Onnx;

using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// Shortens the reference prompt the conditional decoder reads on every call. The prompt is a run of speech
/// tokens plus the mel frames of the same audio (two frames per token, 25 tokens and 50 frames a second); the
/// two stay aligned, and the tail is kept because it is closest to where the new speech continues.
/// </summary>
internal static class PromptTrim
{
    public const int FramesPerToken = 2;

    /// <summary>
    /// The last <paramref name="keepTokens"/> tokens and their frames. Returns the inputs untouched when there
    /// is nothing to cut (keep is zero or negative, or at least as long as the prompt).
    /// </summary>
    /// <param name="tokens">Prompt tokens.</param>
    /// <param name="features">Prompt mel, shape [1, frames, mel].</param>
    public static (long[] Tokens, DenseTensor<float> Features) Tail(long[] tokens, DenseTensor<float> features, int keepTokens)
    {
        if (keepTokens <= 0 || keepTokens >= tokens.Length)
        {
            return (tokens, features);
        }

        var frames = features.Dimensions[1];
        var mel = features.Dimensions[2];
        var keepFrames = Math.Min(frames, keepTokens * FramesPerToken);
        var trimmed = new DenseTensor<float>([1, keepFrames, mel]);
        features.Buffer.Span.Slice((frames - keepFrames) * mel, keepFrames * mel).CopyTo(trimmed.Buffer.Span);
        return (tokens[^keepTokens..], trimmed);
    }
}
