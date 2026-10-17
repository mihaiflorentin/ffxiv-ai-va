namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class ContainerDeviceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiva-cdev", Guid.NewGuid().ToString("N"));

    public ContainerDeviceTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private ServiceContainer Container(
        FakeWebGpuRuntime? runtime = null,
        Func<DevicePreferences>? prefs = null,
        string engine = "kokoro") =>
        new(
            logSinkOverride: new FakeLogSink(),
            modelsDirFactory: () => this.dir,
            selectedEngineFactory: () => engine,
            devicePreferencesFactory: prefs,
            isWineFactory: () => true,
            gpuMarkerPathFactory: () => Path.Combine(this.dir, "gpu-session.pending"),
            webGpuRuntimeFactory: () => runtime ?? new FakeWebGpuRuntime());

    [Fact]
    public void DeviceProbe_IsCached_AndDetectsThroughTheRuntime()
    {
        using var container = this.Container();

        var probe = container.DeviceProbe;

        Assert.Same(probe, container.DeviceProbe);
        Assert.Null(probe.LastReport);
        var report = probe.Detect();
        Assert.Equal(DevicePlatform.Wine, report.Platform);
        Assert.True(report.WebGpuAvailable);
    }

    [Fact]
    public void DeviceReportOrStartProbe_StartsDetectionOnce_AndReturnsTheReportLater()
    {
        var runtime = new FakeWebGpuRuntime();
        using var container = this.Container(runtime);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        DeviceReport? report = null;
        while (report is null && DateTime.UtcNow < deadline)
        {
            report = container.DeviceReportOrStartProbe();
            Thread.Sleep(20);
        }

        Assert.NotNull(report);
        Assert.Equal(1, runtime.EnumerateCalls);
    }

    [Fact]
    public void GpuCrashGuard_UsesTheConfiguredMarkerPath()
    {
        using var container = this.Container();

        var guard = container.GpuCrashGuard;

        Assert.Equal(Path.Combine(this.dir, "gpu-session.pending"), guard.MarkerPath);
        Assert.Same(guard, container.GpuCrashGuard);
    }

    [Fact]
    public void KokoroEngine_ReportsAStatus_WithoutTouchingTheGpu()
    {
        var runtime = new FakeWebGpuRuntime();
        using var container = this.Container(runtime);

        var status = container.SpeechSynthesizer.Status;

        Assert.Equal(EngineState.NotInstalled, status.State);
        Assert.Equal(0, runtime.EnumerateCalls);
        Assert.Equal(DeviceEp.Cpu, status.ActiveDevice.Ep);
    }

    [Fact]
    public void TurboEngine_ReadsTheDeviceSettings_AndNeverLoadsTheRuntimeFromAStatusPoll()
    {
        var runtime = new FakeWebGpuRuntime();
        using var container = this.Container(
            runtime,
            prefs: () => DevicePreferences.Default with { Mode = DeviceMode.Gpu },
            engine: "chatterbox-turbo");

        var status = container.SpeechSynthesizer.Status;

        Assert.Equal(DeviceMode.Gpu, status.RequestedDevice);
        Assert.Equal(EngineState.NotInstalled, status.State); // nothing downloaded in the temp folder
        Assert.Equal(0, runtime.EnumerateCalls);
    }
}
