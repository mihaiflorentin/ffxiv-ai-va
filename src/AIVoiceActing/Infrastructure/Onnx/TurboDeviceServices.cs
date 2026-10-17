namespace AIVoiceActing.Infrastructure.Onnx;

/// <summary>What the Turbo synthesizer needs to follow the device plan instead of a provider string.</summary>
/// <param name="Planner">Decides where the language model may run.</param>
/// <param name="Sessions">Builds per-role session options (the only place a GPU provider is attached).</param>
/// <param name="ClearCrashFlag">Clears the persisted "GPU disabled by crash guard" flag (Retry on GPU).</param>
/// <param name="LmGpuLoopReady">
/// False until the Turbo decode loop runs on device-resident (OrtValue / IoBinding) key-value tensors
/// (ticket #13). The managed loop copies the cache through the host every step and cannot feed the
/// fp16 graphs the GPU path is verified for, so a GPU plan is downgraded to the CPU, with a status
/// that says so, instead of running slower than the CPU would.
/// </param>
public sealed record TurboDeviceServices(
    IDevicePlanner Planner,
    OnnxSessionFactory Sessions,
    Action? ClearCrashFlag = null,
    bool LmGpuLoopReady = false);
