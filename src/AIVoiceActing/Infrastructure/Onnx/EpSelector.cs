namespace AIVoiceActing.Infrastructure.Onnx;

using Microsoft.ML.OnnxRuntime;

/// <summary>
/// Execution-provider selection: macOS → CoreML then CPU; Windows → DirectML then CPU;
/// everything else → CPU. CPU always works, so every failure falls back cleanly with a
/// log line. Only the ORT core package API is used here (CoreML ships with it); DirectML
/// EP registration lives in the Windows plugin build, which references the DirectML native.
/// </summary>
public static class EpSelector
{
    public static string[] CandidateEps()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ["coreml", "cpu"];
        }

        if (OperatingSystem.IsWindows())
        {
            return ["directml", "cpu"];
        }

        return ["cpu"];
    }

    /// <summary>
    /// Builds session options for the requested provider ("coreml"|"cpu"|"directml"|"auto").
    /// Returns the options plus the provider that was actually selected.
    /// </summary>
    public static (SessionOptions Options, string EffectiveEp) CreateSessionOptions(
        string requested,
        Action<string>? log = null)
    {
        var ep = requested.Trim().ToLowerInvariant();
        switch (ep)
        {
            case "auto":
                foreach (var candidate in CandidateEps())
                {
                    var (options, effective) = CreateSessionOptions(candidate, log);
                    if (effective == candidate)
                    {
                        return (options, effective);
                    }

                    options.Dispose();
                }

                return (new SessionOptions(), "cpu");

            case "cpu":
                return (new SessionOptions(), "cpu");

            case "coreml":
                if (!OperatingSystem.IsMacOS())
                {
                    log?.Invoke("CoreML EP requires macOS; falling back to CPU.");
                    return (new SessionOptions(), "cpu");
                }

                try
                {
                    var options = new SessionOptions();
                    // CPU-only CoreML subsets: widest op coverage on the M1; the ANE-only
                    // variant can hard-fail session creation on unsupported TTS ops.
                    options.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_USE_CPU_ONLY);
                    return (options, "coreml");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"CoreML EP unavailable ({ex.Message}); falling back to CPU.");
                    return (new SessionOptions(), "cpu");
                }

            case "directml":
                // DirectML native is Windows-only; outside it (or without the native) this
                // throws and we fall back to CPU. The Windows plugin build registers DML here.
                try
                {
                    var options = new SessionOptions();
                    options.AppendExecutionProvider_DML(0);
                    return (options, "directml");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"DirectML EP unavailable ({ex.Message}); falling back to CPU.");
                    return (new SessionOptions(), "cpu");
                }

            default:
                throw new ArgumentException(
                    $"Unknown execution provider \"{requested}\". Use auto, coreml, cpu, or directml.",
                    nameof(requested));
        }
    }
}
