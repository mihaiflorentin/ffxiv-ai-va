namespace AIVoiceActing.Infrastructure.Onnx;

using Microsoft.ML.OnnxRuntime;

/// <summary>
/// Execution-provider selection: macOS → CPU then CoreML (CoreML only via explicit
/// override — the LM graph partitions into thousands of segments under it); Windows →
/// DirectML then CPU; everything else → CPU. CPU always works, so every failure falls
/// back cleanly with a log line. Only the ORT core package API is used here (CoreML
/// ships with it); DirectML EP registration lives in the Windows plugin build, which
/// references the DirectML native.
/// </summary>
public static class EpSelector
{
    /// <summary>
    /// macOS lists CPU first: CoreML registers but partitions the LM into thousands of
    /// fragments and stalls at run time (gate finding), so "auto" must default to CPU;
    /// coreml stays explicitly selectable. Windows keeps DirectML first.
    /// </summary>
    public static string[] CandidateEps()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ["cpu", "coreml"];
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
        // Leave half the logical cores for the game: ORT defaults to using them all,
        // and CPU-fallback synthesis pinned the whole machine (the "framerate dropped"
        // report). Half keeps synthesis fast enough while the game stays playable.
        static SessionOptions BareOptions() => new()
        {
            IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
        };

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

                return (BareOptions(), "cpu");

            case "cpu":
                return (BareOptions(), "cpu");

            case "coreml":
                if (!OperatingSystem.IsMacOS())
                {
                    log?.Invoke("CoreML EP requires macOS; falling back to CPU.");
                    return (BareOptions(), "cpu");
                }

                try
                {
                    var options = BareOptions();
                    // CPU-only CoreML subsets: widest op coverage on the M1; the ANE-only
                    // variant can hard-fail session creation on unsupported TTS ops.
                    options.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_USE_CPU_ONLY);
                    return (options, "coreml");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"CoreML EP unavailable ({ex.Message}); falling back to CPU.");
                    return (BareOptions(), "cpu");
                }

            case "directml":
                // DirectML native is Windows-only; outside it (or without the native) this
                // throws and we fall back to CPU. The Windows plugin build registers DML here.
                try
                {
                    var options = BareOptions();
                    options.AppendExecutionProvider_DML(0);
                    return (options, "directml");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"DirectML EP unavailable ({ex.Message}); falling back to CPU.");
                    return (BareOptions(), "cpu");
                }

            default:
                throw new ArgumentException(
                    $"Unknown execution provider \"{requested}\". Use auto, coreml, cpu, or directml.",
                    nameof(requested));
        }
    }
}
