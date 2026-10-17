namespace AIVoiceActing.Infrastructure.Onnx;

using Microsoft.ML.OnnxRuntime;

/// <summary>
/// ONNX Runtime's worker threads spin-wait between runs by default, which keeps several cores busy
/// next to the game even when no line is being spoken. Every session the plugin builds turns that off.
/// </summary>
internal static class OrtThreading
{
    public static SessionOptions Quiet(this SessionOptions options)
    {
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return options;
    }
}
