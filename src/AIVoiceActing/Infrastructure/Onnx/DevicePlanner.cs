namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>Seam the engines use to ask "where may this model run right now?".</summary>
public interface IDevicePlanner
{
    /// <summary>The settings in force (read live).</summary>
    DevicePreferences Preferences { get; }

    /// <summary>The probe's last report (display only), or null before the first probe.</summary>
    DeviceReport? LastReport { get; }

    /// <summary>
    /// Plan for one engine and the language-model graph it is about to load. <paramref name="cheap"/>
    /// never hashes files or loads the GPU runtime (UI-thread safe; used for status previews); the
    /// full call may do both and belongs on the engine's load thread.
    /// </summary>
    DevicePlan PlanFor(string engineId, string modelsDir, string lmGraphFile, bool cheap, DeviceMode? modeOverride = null);
}

/// <summary>
/// Glue between the settings, the probe, the allowlist and <see cref="DeviceModePolicy"/>. Engines
/// that never use the GPU skip the expensive inputs entirely.
/// </summary>
public sealed class DevicePlanner : IDevicePlanner
{
    private readonly Func<DevicePreferences> preferences;
    private readonly IDeviceProbe probe;
    private readonly VerifiedGpuCombos allowlist;

    public DevicePlanner(Func<DevicePreferences> preferences, IDeviceProbe probe, VerifiedGpuCombos? allowlist = null)
    {
        this.preferences = preferences;
        this.probe = probe;
        this.allowlist = allowlist ?? new VerifiedGpuCombos();
    }

    public DevicePreferences Preferences => this.preferences();

    public DeviceReport? LastReport => this.probe.LastReport;

    public DevicePlan PlanFor(string engineId, string modelsDir, string lmGraphFile, bool cheap, DeviceMode? modeOverride = null)
    {
        var prefs = this.preferences();
        if (modeOverride is { } forced)
        {
            prefs = prefs with { Mode = forced };
        }

        var report = this.probe.LastReport;
        var verified = false;
        if (DeviceModePolicy.SupportsGpu(engineId) && prefs.Mode != DeviceMode.Cpu)
        {
            verified = this.allowlist.IsVerified(
                VerifiedGpuCombos.WebGpuEpVersion, modelsDir, lmGraphFile, cheap);
            if (report is null && verified && !cheap)
            {
                report = this.probe.Detect();
            }
        }

        return DeviceModePolicy.Plan(new DevicePolicyInput(
            prefs.Mode,
            engineId,
            report ?? DeviceReport.Unknown,
            prefs.GpuDisabledByCrashGuard,
            verified,
            prefs.Preference,
            prefs.Api,
            prefs.Backend));
    }
}
