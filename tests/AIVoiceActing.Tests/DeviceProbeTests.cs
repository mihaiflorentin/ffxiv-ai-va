namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class DeviceProbeTests
{
    private static DeviceProbe Probe(FakeWebGpuRuntime runtime, bool wine = false, bool directMl = false) =>
        new(
            runtime,
            () => wine,
            () => directMl,
            platform: () => wine ? DevicePlatform.Wine : DevicePlatform.Windows);

    [Fact]
    public void LastReport_IsNullUntilDetect()
    {
        var probe = Probe(new FakeWebGpuRuntime());

        Assert.Null(probe.LastReport);
    }

    [Fact]
    public void Detect_ReportsAdaptersFromTheRuntime_AndCachesTheReport()
    {
        var runtime = new FakeWebGpuRuntime();
        var probe = Probe(runtime);

        var report = probe.Detect();

        Assert.True(report.WebGpuAvailable);
        Assert.Equal("Radeon RX 9070 XT", Assert.Single(report.Adapters).Name);
        Assert.Equal(string.Empty, report.Reason);
        Assert.Same(report, probe.LastReport);
    }

    [Fact]
    public void Detect_UnderWine_ReportsWine_AndIgnoresDirectMl()
    {
        var probe = Probe(new FakeWebGpuRuntime(), wine: true, directMl: true);

        var report = probe.Detect();

        Assert.Equal(DevicePlatform.Wine, report.Platform);
        Assert.False(report.DirectMlAvailable);
    }

    [Fact]
    public void Detect_WhenTheRuntimeFailed_CarriesItsReason()
    {
        var runtime = new FakeWebGpuRuntime { Listing = WebGpuEnumeration.Unavailable("No compatible graphics card was found.") };
        var probe = Probe(runtime, wine: true);

        var report = probe.Detect();

        Assert.False(report.WebGpuAvailable);
        Assert.Empty(report.Adapters);
        Assert.Equal("No compatible graphics card was found.", report.Reason);
        Assert.False(report.HasUsableGpu);
    }

    [Fact]
    public void Detect_WebGpuFailed_ButDirectMlPresent_IsStillUsable_AndSaysSo()
    {
        var runtime = new FakeWebGpuRuntime { Listing = WebGpuEnumeration.Unavailable("The GPU runtime could not be loaded.") };
        var report = Probe(runtime, directMl: true).Detect();

        Assert.True(report.DirectMlAvailable);
        Assert.True(report.HasUsableGpu);
        Assert.Contains("DirectML", report.Reason);
    }

    [Fact]
    public void Detect_RunsTheRuntimeEachTime_ButTheRuntimeCachesItself()
    {
        var runtime = new FakeWebGpuRuntime();
        var probe = Probe(runtime);

        probe.Detect();
        probe.Detect();

        Assert.Equal(2, runtime.EnumerateCalls);
    }

    [Fact]
    public void DirectMlCheckThatThrows_IsTreatedAsAbsent()
    {
        var probe = new DeviceProbe(
            new FakeWebGpuRuntime(),
            () => false,
            () => throw new InvalidOperationException("no dml"),
            new FakeLogSink(),
            () => DevicePlatform.Windows);

        var report = probe.Detect();

        Assert.False(report.DirectMlAvailable);
        Assert.True(report.WebGpuAvailable);
    }

    [Fact]
    public void SetupDeviceInfo_FollowsTheReport()
    {
        var gpu = Probe(new FakeWebGpuRuntime()).Detect();
        var none = Probe(new FakeWebGpuRuntime { Listing = WebGpuEnumeration.Unavailable("x") }).Detect();

        var info = AIVoiceActing.UI.State.SetupDeviceInfo.From(gpu);
        Assert.True(info.HasUsableGpu);
        Assert.Equal("Radeon RX 9070 XT", info.GpuName);
        Assert.False(AIVoiceActing.UI.State.SetupDeviceInfo.From(none).HasUsableGpu);
        Assert.False(AIVoiceActing.UI.State.SetupDeviceInfo.From(null).HasUsableGpu);
        Assert.False(AIVoiceActing.UI.State.SetupDeviceInfo.From(gpu, gpuDisabledByCrashGuard: true).HasUsableGpu);
    }

    private static DeviceReport Report(params DeviceAdapter[] adapters) =>
        new(DevicePlatform.Wine, adapters, true, string.Empty);

    [Fact]
    public void AdapterFor_FollowsTheSystemsRanking_NotTheListOrder()
    {
        var igpu = new DeviceAdapter("AMD", "Radeon Graphics", "Vulkan", HighPerformanceIndex: 1);
        var dgpu = new DeviceAdapter("AMD", "Radeon RX 9070 XT", "Vulkan", HighPerformanceIndex: 0);
        var report = Report(igpu, dgpu); // listed the wrong way round

        Assert.Same(dgpu, report.AdapterFor(GpuPreference.Performance));
        Assert.Same(igpu, report.AdapterFor(GpuPreference.LowPower));
    }

    [Fact]
    public void AdapterFor_FallsBackToTheAmountOfGraphicsMemory()
    {
        var igpu = new DeviceAdapter("AMD", "Radeon Graphics", "Vulkan", MemoryMb: 512);
        var dgpu = new DeviceAdapter("AMD", "Radeon RX 9070 XT", "Vulkan", MemoryMb: 16304);

        Assert.Same(dgpu, Report(igpu, dgpu).AdapterFor(GpuPreference.Performance));
        Assert.Same(igpu, Report(igpu, dgpu).AdapterFor(GpuPreference.LowPower));
    }

    [Fact]
    public void AdapterFor_OneAdapterIsTheAnswer_SeveralUnrankedAreNone()
    {
        var one = new DeviceAdapter("AMD", "AMD GPU (device 0x13c0)", "Vulkan", NameIsGuess: true);
        var two = new DeviceAdapter("AMD", "AMD GPU (device 0x7550)", "Vulkan", NameIsGuess: true);

        Assert.Same(one, Report(one).AdapterFor(GpuPreference.Performance));
        Assert.Null(Report(one, two).AdapterFor(GpuPreference.Performance)); // the listing's first entry proves nothing
        Assert.Null(Report().AdapterFor(GpuPreference.Performance));
    }
}
