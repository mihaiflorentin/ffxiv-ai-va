namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Xunit;

public sealed class DeviceModePolicyTests
{
    private const string Turbo = "chatterbox-turbo";

    private static readonly DeviceAdapter Gpu = new("AMD", "Radeon RX 9070 XT", "Vulkan");

    private static DeviceReport Report(
        DevicePlatform platform = DevicePlatform.Windows,
        bool webGpu = true,
        bool directMl = false,
        string reason = "") =>
        new(platform, webGpu || directMl ? [Gpu] : [], webGpu, reason, directMl);

    private static DevicePlan Plan(
        DeviceMode mode = DeviceMode.Auto,
        string engine = Turbo,
        DeviceReport? report = null,
        bool crash = false,
        bool verified = true,
        GpuPreference power = GpuPreference.Performance,
        GpuApi api = GpuApi.Auto,
        DeviceBackend backend = DeviceBackend.Auto) =>
        DeviceModePolicy.Plan(new DevicePolicyInput(mode, engine, report ?? Report(), crash, verified, power, api, backend));

    [Theory]
    [InlineData(DeviceMode.Auto)]
    [InlineData(DeviceMode.Gpu)]
    [InlineData(DeviceMode.Cpu)]
    public void Kokoro_AlwaysRunsOnTheCpu_WhateverTheMode(DeviceMode mode)
    {
        var plan = Plan(mode, engine: "kokoro", verified: true);

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
        Assert.False(plan.WantedGpu);
        Assert.Contains("Kokoro", plan.Reason);
    }

    [Theory]
    [InlineData("f5")]
    [InlineData("chatterbox")]
    [InlineData("remote")]
    public void OtherEngines_StayOnTheCpu(string engine)
    {
        Assert.Equal(DeviceEp.Cpu, Plan(DeviceMode.Gpu, engine).Ep);
    }

    [Fact]
    public void CpuMode_WinsOverEverythingForTurbo()
    {
        var plan = Plan(DeviceMode.Cpu);

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
        Assert.False(plan.WantedGpu);
    }

    [Theory]
    [InlineData(DeviceMode.Auto)]
    [InlineData(DeviceMode.Gpu)]
    public void Turbo_UsesWebGpu_OnNativeWindows_WithD3D12(DeviceMode mode)
    {
        var plan = Plan(mode);

        Assert.Equal(DeviceEp.WebGpu, plan.Ep);
        Assert.Equal(GpuApi.D3D12, plan.Api);
        Assert.True(plan.WantedGpu);
    }

    [Fact]
    public void Wine_ForcesVulkan()
    {
        var plan = Plan(report: Report(DevicePlatform.Wine));

        Assert.Equal(DeviceEp.WebGpu, plan.Ep);
        Assert.Equal(GpuApi.Vulkan, plan.Api);
    }

    [Fact]
    public void NativeLinux_UsesVulkan()
    {
        Assert.Equal(GpuApi.Vulkan, Plan(report: Report(DevicePlatform.Other)).Api);
    }

    [Fact]
    public void ApiOverride_WinsOverThePlatformDefault()
    {
        Assert.Equal(GpuApi.Vulkan, Plan(api: GpuApi.Vulkan).Api);
        Assert.Equal(GpuApi.D3D12, Plan(report: Report(DevicePlatform.Wine), api: GpuApi.D3D12).Api);
    }

    [Theory]
    [InlineData(GpuPreference.Performance)]
    [InlineData(GpuPreference.LowPower)]
    public void PowerPreference_IsCarriedIntoThePlan(GpuPreference power)
    {
        Assert.Equal(power, Plan(power: power).PowerPreference);
    }

    [Fact]
    public void NoAdapter_FallsBackToCpuWithTheProbeReason()
    {
        var plan = Plan(report: Report(webGpu: false, reason: "No compatible graphics card was found."));

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
        Assert.True(plan.WantedGpu);
        Assert.Contains("No compatible graphics card", plan.Reason);
        Assert.Contains("CPU", plan.Reason);
    }

    [Fact]
    public void NoAdapter_AndNoReason_StillExplainsItself()
    {
        var plan = Plan(report: new DeviceReport(DevicePlatform.Windows, [], false, string.Empty));

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void CrashFlag_ForcesCpu_AndExplainsHowToRetry()
    {
        var plan = Plan(DeviceMode.Gpu, crash: true);

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
        Assert.True(plan.WantedGpu);
        Assert.Contains("Retry on GPU", plan.Reason);
    }

    [Fact]
    public void CrashFlag_DoesNotMatterWhenTheUserChoseCpu()
    {
        var plan = Plan(DeviceMode.Cpu, crash: true);

        Assert.False(plan.WantedGpu);
        Assert.DoesNotContain("Retry", plan.Reason);
    }

    [Fact]
    public void UnverifiedModel_RunsOnTheCpu_EvenWithAHealthyGpu()
    {
        var plan = Plan(DeviceMode.Gpu, verified: false);

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
        Assert.True(plan.WantedGpu);
        Assert.Contains("tested", plan.Reason);
    }

    [Fact]
    public void CrashFlag_TakesPrecedenceOverTheAllowlistMessage()
    {
        Assert.Contains("Retry on GPU", Plan(crash: true, verified: false).Reason);
    }

    [Fact]
    public void WebGpuMissing_OnNativeWindows_FallsBackToDirectMl()
    {
        var plan = Plan(report: Report(webGpu: false, directMl: true, reason: "The GPU runtime could not be loaded."));

        Assert.Equal(DeviceEp.DirectML, plan.Ep);
        Assert.True(plan.WantedGpu);
    }

    [Fact]
    public void WebGpuMissing_UnderWine_NeverTriesDirectMl()
    {
        var plan = Plan(report: Report(DevicePlatform.Wine, webGpu: false, directMl: true, reason: "No compatible graphics card was found."));

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
    }

    [Fact]
    public void WebGpuAvailable_IsPreferredOverDirectMl()
    {
        Assert.Equal(DeviceEp.WebGpu, Plan(report: Report(directMl: true)).Ep);
    }

    [Fact]
    public void ForcedWebGpuBackend_DoesNotFallBackToDirectMl()
    {
        var plan = Plan(report: Report(webGpu: false, directMl: true), backend: DeviceBackend.WebGpu);

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
    }

    [Fact]
    public void ForcedDirectMl_OnNativeWindows_SkipsWebGpu()
    {
        var plan = Plan(report: Report(directMl: true), backend: DeviceBackend.DirectML);

        Assert.Equal(DeviceEp.DirectML, plan.Ep);
    }

    [Fact]
    public void ForcedDirectMl_UnderWine_ExplainsItself()
    {
        var plan = Plan(report: Report(DevicePlatform.Wine, directMl: true), backend: DeviceBackend.DirectML);

        Assert.Equal(DeviceEp.Cpu, plan.Ep);
        Assert.Contains("Wine", plan.Reason);
    }

    [Fact]
    public void EngineIdMatch_IgnoresCase()
    {
        Assert.True(DeviceModePolicy.SupportsGpu("Chatterbox-Turbo"));
        Assert.False(DeviceModePolicy.SupportsGpu("kokoro"));
    }
}
