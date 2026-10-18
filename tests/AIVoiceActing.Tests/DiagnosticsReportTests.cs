namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class DiagnosticsReportTests
{
    private static DiagnosticsInput Input(
        EngineStatus? turboStatus = null,
        DeviceReport? report = null,
        DeviceSettings? device = null) =>
        new(
            "0.0.29",
            2,
            "Balanced",
            device ?? new DeviceSettings(),
            "Kokoro",
            [
                new DiagnosticsEngine("Kokoro", EngineStatus.FromReadiness(true, string.Empty), true),
                new DiagnosticsEngine("Chatterbox Turbo", turboStatus, turboStatus is not null),
                new DiagnosticsEngine("Remote server", null, true),
            ],
            report,
            Downloading: false,
            ModelsFolderBytes: 3L * 1024 * 1024 * 1024,
            FreeDiskBytes: 50L * 1024 * 1024 * 1024,
            QueueDepth: 2,
            ProcessMemoryBytes: 2L * 1024 * 1024 * 1024);

    [Fact]
    public void Report_CarriesTheFactsASupporterNeeds()
    {
        var report = DiagnosticsReport.Build(Input(
            report: new DeviceReport(
                DevicePlatform.Wine,
                [new DeviceAdapter("AMD", "AMD Radeon RX 7800 XT", "Vulkan")],
                WebGpuAvailable: true,
                Reason: string.Empty)));

        Assert.Contains("Plugin version: 0.0.29", report);
        Assert.Contains("Voice quality: Balanced", report);
        Assert.Contains("Processor setting: auto", report);
        Assert.Contains("Platform: Wine", report);
        Assert.Contains("AMD Radeon RX 7800 XT (Vulkan)", report);
        Assert.Contains("reported by the system", report);
        Assert.Contains("Models folder size: 3.0 GB", report);
    }

    [Fact]
    public void Report_CopiesThePortsReasonVerbatim()
    {
        var reason = "The GPU could not be started, so Chatterbox Turbo runs on the CPU.";
        var status = new EngineStatus(EngineState.FellBack, DeviceMode.Auto, ActiveDevice.Cpu, reason, CanRetryGpu: true);

        var report = DiagnosticsReport.Build(Input(status));

        Assert.Contains(reason, report);
        Assert.Contains("FellBack", report);
        Assert.Contains("retry on the graphics card is possible", report);
    }

    [Fact]
    public void Report_SaysWhatIsInstalledButNotRunning()
    {
        var report = DiagnosticsReport.Build(Input());

        Assert.Contains("Remote server: installed, not running", report);
        Assert.Contains("Chatterbox Turbo: not installed", report);
    }

    [Fact]
    public void Report_NamesTheGraphicsCardAnEngineUses()
    {
        var status = new EngineStatus(
            EngineState.Ready, DeviceMode.Auto, new ActiveDevice(DeviceEp.WebGpu, "RTX 4070"), string.Empty, false);

        Assert.Contains("running on the graphics card (RTX 4070)", DiagnosticsReport.Build(Input(status)));
    }

    [Fact]
    public void Report_HandlesNoProbeAndNoAdapter()
    {
        Assert.Contains("Not checked yet.", DiagnosticsReport.Build(Input(report: null)));
        var none = new DeviceReport(DevicePlatform.Windows, [], false, "No GPU adapter was found.");

        var report = DiagnosticsReport.Build(Input(report: none));

        Assert.Contains("None found.", report);
        Assert.Contains("No GPU adapter was found.", report);
    }

    [Fact]
    public void Report_ShowsTheCrashGuard()
    {
        var report = DiagnosticsReport.Build(Input(device: new DeviceSettings { GpuDisabledByCrashGuard = true }));

        Assert.Contains("Crash guard tripped: yes", report);
    }

    [Fact]
    public void Report_NamesNoFolderPaths()
    {
        var report = DiagnosticsReport.Build(Input());

        Assert.DoesNotContain("/home", report);
        Assert.DoesNotContain(":\\", report);
        Assert.DoesNotContain(".xlcore", report);
    }

    [Fact]
    public void Report_HandlesAnUnknownDisk()
    {
        var input = Input() with { FreeDiskBytes = long.MaxValue };

        Assert.Contains("Free space: unknown", DiagnosticsReport.Build(input));
    }

    [Fact]
    public void WhenLinesRunOnTheProcessorForNow_TheReportSaysSoAndGivesTheSpeedOfEachDevice()
    {
        var status = new EngineStatus(
            EngineState.Ready,
            DeviceMode.Auto,
            ActiveDevice.Cpu,
            "Running on your processor right now.",
            false,
            OnProcessorForNow: true,
            Rates: new LmDeviceRates(45.4, 12.2));

        var report = DiagnosticsReport.Build(Input(turboStatus: status));

        Assert.Contains("running on the processor for now", report);
        Assert.Contains("graphics card 45 ms, processor 12 ms", report);
    }

    [Fact]
    public void ADeviceThatWasNeverMeasured_IsSaidSo()
    {
        var status = new EngineStatus(
            EngineState.Ready,
            DeviceMode.Auto,
            new ActiveDevice(DeviceEp.WebGpu),
            string.Empty,
            false,
            Rates: new LmDeviceRates(8.0, 0));

        var report = DiagnosticsReport.Build(Input(turboStatus: status));

        Assert.Contains("graphics card 8 ms, processor not measured", report);
    }
}
