namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>
/// What the setup flow knows about the graphics card. <see cref="Unknown"/> (nothing probed yet, no
/// usable GPU) makes the recommendation Fast; <see cref="From"/> turns the device probe's report into
/// the real answer so Home's recommendation sees the GPU.
/// </summary>
public sealed record SetupDeviceInfo(bool HasUsableGpu, string? GpuName)
{
    public static SetupDeviceInfo Unknown { get; } = new(false, null);

    /// <summary>
    /// From a probe report (null while detection is still running). A crash-guard trip counts as
    /// "no usable GPU" until the user retries, so the recommendation does not promise what is switched off.
    /// </summary>
    public static SetupDeviceInfo From(DeviceReport? report, bool gpuDisabledByCrashGuard = false)
    {
        if (report is null || gpuDisabledByCrashGuard || !report.HasUsableGpu)
        {
            return Unknown;
        }

        var name = report.Adapters.Select(a => a.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        return new SetupDeviceInfo(true, name);
    }
}
