namespace AIVoiceActing.Ports;

/// <summary>Where the plugin is running. Wine is the XIVLauncher / Proton host on Linux and macOS.</summary>
public enum DevicePlatform
{
    Windows,
    Wine,
    Other,
}

/// <summary>What the user asked for in settings (<c>Device.Mode</c>). Auto = each engine's recommended device.</summary>
public enum DeviceMode
{
    Auto,
    Gpu,
    Cpu,
}

/// <summary>Execution provider a session will use.</summary>
public enum DeviceEp
{
    Cpu,
    WebGpu,
    DirectML,
}

/// <summary>Graphics API the WebGPU runtime (Dawn) is forced onto. Auto = decide from the platform.</summary>
public enum GpuApi
{
    Auto,
    Vulkan,
    D3D12,
}

/// <summary>
/// Which adapter class Dawn prefers. The runtime cannot pin a specific adapter, only a class:
/// the discrete GPU (<see cref="Performance"/>) or the integrated one (<see cref="LowPower"/>).
/// </summary>
public enum GpuPreference
{
    Performance,
    LowPower,
}

/// <summary>Advanced backend override (<c>Device.Backend</c>).</summary>
public enum DeviceBackend
{
    Auto,
    WebGpu,
    DirectML,
}

/// <summary>The settings that decide where an engine runs, read live from the configuration.</summary>
/// <param name="GpuDisabledByCrashGuard">A previous run died inside GPU code; GPU stays off until the user retries.</param>
public sealed record DevicePreferences(
    DeviceMode Mode,
    GpuPreference Preference,
    GpuApi Api,
    DeviceBackend Backend,
    bool GpuDisabledByCrashGuard)
{
    public static DevicePreferences Default { get; } =
        new(DeviceMode.Auto, GpuPreference.Performance, GpuApi.Auto, DeviceBackend.Auto, false);
}

/// <summary>One graphics adapter, for display only (the runtime cannot be told to use a specific one).</summary>
/// <param name="Vendor">AMD, NVIDIA, Intel, or the raw vendor text.</param>
/// <param name="Name">Adapter name, or a PCI id description when the platform gives no name.</param>
/// <param name="Backend">Graphics API the adapter was enumerated through (Vulkan, D3D12, unknown).</param>
public sealed record DeviceAdapter(string Vendor, string Name, string Backend);

/// <summary>What the machine can do. Immutable; <see cref="Reason"/> is final user-facing text.</summary>
/// <param name="WebGpuAvailable">The WebGPU runtime loaded and found at least one GPU.</param>
/// <param name="DirectMlAvailable">Native Windows only: the DirectML runtime is present.</param>
/// <param name="Reason">Why the GPU cannot be used; empty when <see cref="WebGpuAvailable"/>.</param>
public sealed record DeviceReport(
    DevicePlatform Platform,
    IReadOnlyList<DeviceAdapter> Adapters,
    bool WebGpuAvailable,
    string Reason,
    bool DirectMlAvailable = false)
{
    /// <summary>Nothing probed yet.</summary>
    public static DeviceReport Unknown { get; } =
        new(DevicePlatform.Other, [], false, "The graphics card has not been checked yet.");

    /// <summary>Any GPU path (WebGPU or DirectML) that could run a verified model.</summary>
    public bool HasUsableGpu => (this.WebGpuAvailable || this.DirectMlAvailable) && this.Adapters.Count > 0;
}

/// <summary>Driven port: finds out which GPU paths this machine offers.</summary>
public interface IDeviceProbe
{
    /// <summary>The last report, or null before the first <see cref="Detect"/>. Cheap; safe to poll per frame.</summary>
    DeviceReport? LastReport { get; }

    /// <summary>Runs detection now (loads the GPU runtime, enumerates adapters) and stores the result.</summary>
    DeviceReport Detect();
}
