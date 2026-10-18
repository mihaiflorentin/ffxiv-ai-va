namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class OnnxSessionFactoryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiva-factory", Guid.NewGuid().ToString("N"));

    private static readonly DevicePlan GpuPlan =
        new(DeviceEp.WebGpu, GpuApi.Vulkan, GpuPreference.Performance, "gpu", true);

    private string Marker => Path.Combine(this.dir, "gpu-session.pending");

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private OnnxSessionFactory Factory(FakeWebGpuRuntime runtime) =>
        new(runtime, new GpuCrashGuard(this.Marker), new FakeLogSink());

    [Theory]
    [InlineData(SessionRole.Encoder)]
    [InlineData(SessionRole.Decoder)]
    [InlineData(SessionRole.Embedding)]
    [InlineData(SessionRole.Kokoro)]
    public void EveryRoleExceptTheLm_IsCpu_EvenForAGpuPlan(SessionRole role)
    {
        var runtime = new FakeWebGpuRuntime();

        using var build = Factory(runtime).Create(role, GpuPlan).Options;

        Assert.Empty(runtime.Appended);
        Assert.False(File.Exists(this.Marker));
    }

    [Theory]
    [InlineData(SessionRole.Encoder)]
    [InlineData(SessionRole.Kokoro)]
    public void NonLmRoles_ReportCpu(SessionRole role)
    {
        var build = Factory(new FakeWebGpuRuntime()).Create(role, GpuPlan);
        using var options = build.Options;

        Assert.Equal(DeviceEp.Cpu, build.Ep);
        Assert.Null(build.Guard);
        Assert.Equal(string.Empty, build.FallbackReason);
    }

    [Fact]
    public void LmOnACpuPlan_IsCpu_WithNoMarker()
    {
        var runtime = new FakeWebGpuRuntime();
        var build = Factory(runtime).Create(SessionRole.Lm, DevicePlan.OnCpu("cpu"));
        using var options = build.Options;

        Assert.Equal(DeviceEp.Cpu, build.Ep);
        Assert.Empty(runtime.Appended);
        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void LmOnAWebGpuPlan_WritesTheMarkerBeforeAttaching_AndKeepsItUntilTheScopeCompletes()
    {
        var runtime = new FakeWebGpuRuntime();
        var build = Factory(runtime).Create(SessionRole.Lm, GpuPlan);
        using var options = build.Options;

        Assert.Equal(DeviceEp.WebGpu, build.Ep);
        Assert.Same(GpuPlan, Assert.Single(runtime.Appended));
        Assert.True(File.Exists(this.Marker));
        Assert.NotNull(build.Guard);

        build.Guard!.Succeeded();

        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void ManagedFailureAttachingTheGpu_FallsBackToCpu_AndClearsTheMarker()
    {
        var runtime = new FakeWebGpuRuntime { AppendThrows = new InvalidOperationException("No supported adapters") };
        var build = Factory(runtime).Create(SessionRole.Lm, GpuPlan);
        using var options = build.Options;

        Assert.Equal(DeviceEp.Cpu, build.Ep);
        Assert.Contains("CPU", build.FallbackReason);
        Assert.Null(build.Guard);
        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void WithoutAWritableMarker_TheGpuIsRefused()
    {
        Directory.CreateDirectory(this.dir);
        var blocker = Path.Combine(this.dir, "blocker");
        File.WriteAllText(blocker, "x");
        var runtime = new FakeWebGpuRuntime();
        var factory = new OnnxSessionFactory(runtime, new GpuCrashGuard(Path.Combine(blocker, "m")), new FakeLogSink());

        var build = factory.Create(SessionRole.Lm, GpuPlan);
        using var options = build.Options;

        Assert.Equal(DeviceEp.Cpu, build.Ep);
        Assert.Empty(runtime.Appended);
        Assert.Contains("safety marker", build.FallbackReason);
    }

    [Fact]
    public void TheDecoder_NeverTakesMoreThanTwoThreads_WhateverTheBackgroundLoad()
    {
        var factory = Factory(new FakeWebGpuRuntime());

        using var byDefault = factory.Create(SessionRole.Decoder, GpuPlan).Options;
        using var eight = factory.Create(SessionRole.Decoder, GpuPlan, intraOpThreads: 8).Options;
        using var four = factory.Create(SessionRole.Decoder, GpuPlan, intraOpThreads: 4).Options;
        using var one = factory.Create(SessionRole.Decoder, GpuPlan, intraOpThreads: 1).Options;
        using var encoder = factory.Create(SessionRole.Encoder, GpuPlan, intraOpThreads: 8).Options;

        Assert.InRange(byDefault.IntraOpNumThreads, 2, OnnxSessionFactory.DecoderDefaultMaxThreads);
        Assert.Equal(2, eight.IntraOpNumThreads);
        Assert.Equal(2, four.IntraOpNumThreads);
        Assert.Equal(1, one.IntraOpNumThreads);
        Assert.Equal(8, encoder.IntraOpNumThreads); // other roles keep the background load setting
    }

    [Fact]
    public void TheSpinningSwitch_KeepsTheDecoderThreadCap()
    {
        var spinning = new OnnxSessionFactory(new FakeWebGpuRuntime(), new GpuCrashGuard(Path.Combine(this.dir, "m2")), new FakeLogSink(), () => true);

        using var decoder = spinning.Create(SessionRole.Decoder, GpuPlan, intraOpThreads: 8).Options;

        Assert.Equal(2, decoder.IntraOpNumThreads);
    }

    [Fact]
    public void OnACpuPlan_TheDecoderKeepsTheBackgroundLoadThreadCount()
    {
        using var decoder = Factory(new FakeWebGpuRuntime()).Create(SessionRole.Decoder, DevicePlan.OnCpu("cpu"), intraOpThreads: 8).Options;

        Assert.Equal(8, decoder.IntraOpNumThreads);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(16, 2)]
    public void DecoderThreads_FollowsTheRule(int? explicitThreads, int expected)
    {
        var threads = OnnxSessionFactory.DecoderThreads(explicitThreads);

        Assert.Equal(expected, threads);
    }
}
