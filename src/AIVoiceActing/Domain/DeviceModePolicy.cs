namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Where one engine's heavy session runs. <see cref="Reason"/> is final user-facing text.
/// </summary>
/// <param name="Api">Resolved API for <see cref="DeviceEp.WebGpu"/>; <see cref="GpuApi.Auto"/> for the others.</param>
/// <param name="PowerPreference">Adapter class Dawn should prefer (WebGPU only).</param>
/// <param name="WantedGpu">The settings and the engine's default asked for the GPU (so a CPU plan is a fallback).</param>
public sealed record DevicePlan(
    DeviceEp Ep,
    GpuApi Api,
    GpuPreference PowerPreference,
    string Reason,
    bool WantedGpu = false)
{
    public bool IsGpu => this.Ep != DeviceEp.Cpu;

    public static DevicePlan OnCpu(string reason, bool wantedGpu = false, GpuPreference power = GpuPreference.Performance) =>
        new(DeviceEp.Cpu, GpuApi.Auto, power, reason, wantedGpu);
}

/// <summary>Everything the policy decides from.</summary>
/// <param name="Mode">The user's Device.Mode.</param>
/// <param name="EngineId">Canonical engine id.</param>
/// <param name="Report">Probe result.</param>
/// <param name="CrashGuardTripped">A previous run crashed inside GPU code and the user has not retried.</param>
/// <param name="AllowlistMatch">The model files about to be used are a pinned, verified (EP version, hash) combination.</param>
public sealed record DevicePolicyInput(
    DeviceMode Mode,
    string EngineId,
    DeviceReport Report,
    bool CrashGuardTripped,
    bool AllowlistMatch,
    GpuPreference Preference = GpuPreference.Performance,
    GpuApi ApiOverride = GpuApi.Auto,
    DeviceBackend BackendOverride = DeviceBackend.Auto);

/// <summary>
/// Pure decision table: settings x engine x probe x crash flag x allowlist -> <see cref="DevicePlan"/>.
/// Only the Turbo language model has a GPU path; every other engine and session stays on the CPU.
/// A GPU is used only when nothing says no: the model must be a verified combination (a wrong
/// graph on the WebGPU runtime aborts the whole game process, so unverified means CPU).
/// </summary>
public static class DeviceModePolicy
{
    public const string TurboEngineId = "chatterbox-turbo";

    /// <summary>Engines with a GPU path. Kokoro is not one: its LSTM nodes abort the WebGPU runtime.</summary>
    public static bool SupportsGpu(string engineId) =>
        string.Equals(engineId, TurboEngineId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Vulkan under Wine (D3D12 is not implemented there), D3D12 on native Windows.</summary>
    public static GpuApi DefaultApi(DevicePlatform platform) =>
        platform == DevicePlatform.Windows ? GpuApi.D3D12 : GpuApi.Vulkan;

    public static DevicePlan Plan(DevicePolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var power = input.Preference;
        if (!SupportsGpu(input.EngineId))
        {
            return DevicePlan.OnCpu(
                string.Equals(input.EngineId, "kokoro", StringComparison.OrdinalIgnoreCase)
                    ? "Kokoro always runs on the CPU, where it is already faster than real time."
                    : "This voice engine runs on the CPU.",
                wantedGpu: false,
                power);
        }

        if (input.Mode == DeviceMode.Cpu)
        {
            return DevicePlan.OnCpu("Running on the CPU, as you chose.", wantedGpu: false, power);
        }

        // From here on the GPU was wanted (Auto means Turbo's recommended device, the GPU).
        if (input.CrashGuardTripped)
        {
            return DevicePlan.OnCpu(
                "The game stopped while using the GPU last time, so the GPU is turned off. Use Retry on GPU to try again.",
                wantedGpu: true,
                power);
        }

        if (!input.AllowlistMatch)
        {
            return DevicePlan.OnCpu(
                "The GPU is only used with voice models that have been tested with it; the installed one runs on the CPU.",
                wantedGpu: true,
                power);
        }

        var report = input.Report;
        var native = report.Platform == DevicePlatform.Windows;
        var backend = input.BackendOverride;

        if (backend == DeviceBackend.DirectML)
        {
            return native && report.DirectMlAvailable && report.Adapters.Count > 0
                ? new DevicePlan(DeviceEp.DirectML, GpuApi.Auto, power, "Using the GPU through DirectML.", WantedGpu: true)
                : DevicePlan.OnCpu(
                    report.Platform == DevicePlatform.Wine
                        ? "DirectML does not work under Wine, so this runs on the CPU."
                        : "DirectML is not available on this computer, so this runs on the CPU.",
                    wantedGpu: true,
                    power);
        }

        if (report.WebGpuAvailable && report.Adapters.Count > 0)
        {
            var api = input.ApiOverride != GpuApi.Auto ? input.ApiOverride : DefaultApi(report.Platform);
            var adapter = power == GpuPreference.LowPower ? "the low-power GPU" : "the high-performance GPU";
            return new DevicePlan(
                DeviceEp.WebGpu, api, power, $"Using {adapter} through {api}.", WantedGpu: true);
        }

        // WebGPU did not come up. Native Windows can still use DirectML (unless WebGPU was forced).
        if (backend == DeviceBackend.Auto && native && report.DirectMlAvailable)
        {
            return new DevicePlan(
                DeviceEp.DirectML,
                GpuApi.Auto,
                power,
                "The WebGPU runtime is unavailable, so the GPU is used through DirectML.",
                WantedGpu: true);
        }

        var why = string.IsNullOrWhiteSpace(report.Reason) ? "No compatible graphics card was found." : report.Reason.Trim();
        return DevicePlan.OnCpu($"{why} Running on the CPU.", wantedGpu: true, power);
    }
}
