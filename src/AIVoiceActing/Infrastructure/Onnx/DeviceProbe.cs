namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;

/// <summary>
/// <see cref="IDeviceProbe"/> over the WebGPU runtime: loads it, lists the GPUs it sees, and notes
/// whether DirectML is a fallback (native Windows only). There is deliberately no self-test: a
/// toy graph passes while the real graph can still abort the process, so safety comes from the
/// verified-model allowlist and the crash guard instead.
/// </summary>
public sealed class DeviceProbe : IDeviceProbe
{
    private readonly IWebGpuRuntime runtime;
    private readonly Func<bool> isWine;
    private readonly Func<DevicePlatform>? platformOverride;
    private readonly Func<bool> directMlPresent;
    private readonly ILogSink? log;
    private readonly object gate = new();
    private volatile DeviceReport? last;

    public DeviceProbe(
        IWebGpuRuntime runtime,
        Func<bool>? isWine = null,
        Func<bool>? directMlPresent = null,
        ILogSink? log = null,
        Func<DevicePlatform>? platform = null)
    {
        this.platformOverride = platform;
        this.runtime = runtime;
        this.isWine = isWine ?? (() => RuntimeHost.DetectWine());
        this.directMlPresent = directMlPresent ?? DefaultDirectMlPresent;
        this.log = log;
    }

    public DeviceReport? LastReport => this.last;

    public DeviceReport Detect()
    {
        lock (this.gate)
        {
            var platform = this.platformOverride?.Invoke() ?? RuntimeHost.PlatformOf(this.isWine());
            var listing = this.runtime.Enumerate();
            var adapters = listing.Adapters.ToList();

            // DirectML cannot work under Wine (no D3D12 adapter), so only native Windows counts.
            var directMl = false;
            if (platform == DevicePlatform.Windows)
            {
                try
                {
                    directMl = this.directMlPresent();
                }
                catch (Exception ex)
                {
                    this.log?.Warn($"DirectML check failed: {ex.Message}");
                }

                if (directMl && adapters.Count == 0)
                {
                    adapters.Add(new DeviceAdapter(string.Empty, "Graphics card (DirectML)", "D3D12"));
                }
            }

            var reason = listing.Available
                ? string.Empty
                : directMl ? $"{listing.Failure} DirectML is available instead." : listing.Failure;
            var report = new DeviceReport(platform, adapters, listing.Available, reason, directMl);
            this.last = report;
            this.log?.Info(
                $"Device probe: {platform}, {adapters.Count} adapter(s), WebGPU {(listing.Available ? "available" : "unavailable")}, DirectML {(directMl ? "available" : "no")}.");
            return report;
        }
    }

    private static bool DefaultDirectMlPresent() =>
        OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider");
}
