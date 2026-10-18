namespace AIVoiceActing.Infrastructure.Onnx;

using Microsoft.ML.OnnxRuntime;

/// <summary>
/// ONNX Runtime's worker threads spin-wait between runs by default, which keeps several cores busy
/// next to the game even when no line is being spoken. Every session the plugin builds turns that off, except
/// that the conditional decoder can opt back in (Advanced, off by default) to measure whether its many short
/// parallel sections run faster when two workers spin than when they park and wake through the system.
/// </summary>
internal static class OrtThreading
{
    public static SessionOptions Quiet(this SessionOptions options)
    {
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return options;
    }

    /// <summary>Lets this session's worker threads spin-wait again (only the decoder, only when the player asks).</summary>
    public static SessionOptions WithSpinning(this SessionOptions options)
    {
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "1");
        return options;
    }
}
