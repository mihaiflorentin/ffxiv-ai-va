namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;

/// <summary>What the Turbo synthesizer needs to follow the device plan instead of a provider string.</summary>
/// <param name="Planner">Decides where the language model may run.</param>
/// <param name="Sessions">Builds per-role session options (the only place a GPU provider is attached).</param>
/// <param name="ClearCrashFlag">Clears the persisted "GPU disabled by crash guard" flag (Retry on GPU).</param>
/// <param name="LmGpuLoopReady">
/// True when the decode loop keeps the key-value cache device-resident (<see cref="TurboDecodeLoop"/>).
/// When false a GPU plan is reported and built as a CPU run, so a caller that has not wired the loop
/// never runs the slow host round trip.
/// </param>
/// <param name="Precision">Live <c>Engine.PerEngine[chatterbox-turbo].Precision</c> (auto, fp32, fp16, q4f16); null = auto.</param>
/// <param name="ModeOverride">Live per-engine device choice (inherit = null, cpu, gpu); a Retry still wins over it.</param>
/// <param name="OpenSession">Test seam over <c>new InferenceSession(path, options)</c>.</param>
public sealed record TurboDeviceServices(
    IDevicePlanner Planner,
    OnnxSessionFactory Sessions,
    Action? ClearCrashFlag = null,
    bool LmGpuLoopReady = false,
    Func<string>? Precision = null,
    Func<DeviceMode?>? ModeOverride = null,
    Func<string, SessionOptions, InferenceSession>? OpenSession = null);
