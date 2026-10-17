namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;

/// <summary>Which session of an engine is being created. Only <see cref="Lm"/> ever leaves the CPU.</summary>
public enum SessionRole
{
    /// <summary>Turbo language model: the one graph with a verified GPU path.</summary>
    Lm,

    /// <summary>Speech encoder: wrong output on the verified runtime version; CPU.</summary>
    Encoder,

    /// <summary>Conditional decoder: STFT and random-noise nodes abort the runtime; CPU.</summary>
    Decoder,

    /// <summary>Token embedding: tiny; CPU.</summary>
    Embedding,

    /// <summary>Kokoro: LSTM nodes abort the runtime; CPU, whatever the plan says.</summary>
    Kokoro,
}

/// <summary>
/// Session options for one role. The caller owns <see cref="Options"/> and must complete or fail
/// <see cref="Guard"/> (when not null): complete it after the first successful inference, fail it
/// on a managed exception. The marker stays on disk until then, which is the crash guard's whole point.
/// </summary>
/// <param name="Ep">Provider actually attached.</param>
/// <param name="DeviceMemory">Where a key-value cache must live to stay on the GPU; null on the CPU (and for DirectML, which copies).</param>
/// <param name="FallbackReason">Plain-text reason when the plan wanted a GPU and this is a CPU build; else empty.</param>
public sealed record SessionBuild(
    SessionOptions Options,
    DeviceEp Ep,
    string FallbackReason,
    GpuCrashGuard.Scope? Guard,
    OrtMemoryInfo? DeviceMemory = null)
{
    public bool IsGpu => this.Ep != DeviceEp.Cpu;
}

/// <summary>
/// Turns a <see cref="DevicePlan"/> into per-role <see cref="SessionOptions"/>. This is the single
/// place where a GPU provider is attached: it writes the crash marker first, never gives the
/// encoder, decoder, embedding or Kokoro sessions a GPU, and turns any managed failure into a CPU
/// build with a reason (native aborts are the crash guard's job).
/// </summary>
public sealed class OnnxSessionFactory
{
    private readonly IWebGpuRuntime runtime;
    private readonly GpuCrashGuard guard;
    private readonly ILogSink? log;

    public OnnxSessionFactory(IWebGpuRuntime runtime, GpuCrashGuard guard, ILogSink? log = null)
    {
        this.runtime = runtime;
        this.guard = guard;
        this.log = log;
    }

    public SessionBuild Create(SessionRole role, DevicePlan plan, int? intraOpThreads = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (role != SessionRole.Lm || !plan.IsGpu)
        {
            return Cpu(role == SessionRole.Decoder ? DecoderThreads(intraOpThreads) : intraOpThreads, string.Empty);
        }

        if (!this.guard.TryBegin($"{plan.Ep} session for the language model", out var scope))
        {
            return Cpu(intraOpThreads, "The GPU safety marker could not be written, so this runs on the CPU.");
        }

        SessionOptions? options = null;
        try
        {
            if (plan.Ep == DeviceEp.WebGpu)
            {
                options = BareOptions(intraOpThreads);
                this.runtime.Append(options, plan);
                return new SessionBuild(options, DeviceEp.WebGpu, string.Empty, scope, this.runtime.DeviceMemory);
            }

            var (dml, effective) = EpSelector.CreateSessionOptions("directml", m => this.log?.Info(m), intraOpThreads);
            if (effective == "directml")
            {
                return new SessionBuild(dml, DeviceEp.DirectML, string.Empty, scope);
            }

            dml.Dispose();
            scope.FailedSafely();
            return Cpu(intraOpThreads, "DirectML could not start, so this runs on the CPU.");
        }
        catch (Exception ex)
        {
            this.log?.Warn($"GPU provider could not be attached ({ex.GetType().Name}: {ex.Message}); using the CPU.");
            options?.Dispose();
            scope.FailedSafely();
            return Cpu(intraOpThreads, "The GPU could not be started, so this runs on the CPU.");
        }
    }

    /// <summary>
    /// Most intra-op threads the conditional decoder takes by default. Its calls are short bursts of small
    /// matrices (one per chunk of a streamed line) that already run faster than real time on two threads,
    /// where more threads mostly spin: the same call costs 0.7 CPU seconds on one thread, 1.2 on two, 1.9 on
    /// four and 3.5 on eight (docs/plans/2026-10-gpu-spike-results.md), and the CPU is shared with the game.
    /// An explicit thread setting (the CPU impact choice) is respected as given.
    /// </summary>
    public const int DecoderDefaultMaxThreads = 2;

    /// <summary>The decoder's thread count: the explicit setting, else half the logical cores capped at <see cref="DecoderDefaultMaxThreads"/>.</summary>
    public static int DecoderThreads(int? explicitThreads) =>
        explicitThreads is { } threads && threads > 0
            ? threads
            : Math.Min(DecoderDefaultMaxThreads, Math.Max(2, Environment.ProcessorCount / 2));

    private static SessionBuild Cpu(int? intraOpThreads, string fallbackReason) =>
        new(EpSelector.CreateSessionOptions("cpu", intraOpThreads: intraOpThreads).Options, DeviceEp.Cpu, fallbackReason, null);

    private static SessionOptions BareOptions(int? intraOpThreads) => new()
    {
        IntraOpNumThreads = intraOpThreads is { } threads && threads > 0
            ? threads
            : Math.Max(2, Environment.ProcessorCount / 2),
    };
}
