namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Xunit;

/// <summary>
/// The WebGPU runtime silently ignores unknown or prefixed option keys (a typo never errors), so the
/// exact strings are pinned here.
/// </summary>
public sealed class WebGpuOptionsTests
{
    [Fact]
    public void Keys_AreTheExactUnprefixedSpellings()
    {
        Assert.Equal("dawnBackendType", WebGpuOptions.DawnBackendTypeKey);
        Assert.Equal("powerPreference", WebGpuOptions.PowerPreferenceKey);
        Assert.Equal("Vulkan", WebGpuOptions.BackendVulkan);
        Assert.Equal("D3D12", WebGpuOptions.BackendD3D12);
        Assert.Equal("high-performance", WebGpuOptions.PowerHigh);
        Assert.Equal("low-power", WebGpuOptions.PowerLow);
    }

    [Theory]
    [InlineData(GpuApi.Vulkan, GpuPreference.Performance, "Vulkan", "high-performance")]
    [InlineData(GpuApi.Vulkan, GpuPreference.LowPower, "Vulkan", "low-power")]
    [InlineData(GpuApi.D3D12, GpuPreference.Performance, "D3D12", "high-performance")]
    [InlineData(GpuApi.D3D12, GpuPreference.LowPower, "D3D12", "low-power")]
    public void Build_MapsApiAndPowerPreference(GpuApi api, GpuPreference power, string backend, string powerValue)
    {
        var options = WebGpuOptions.Build(new DevicePlan(DeviceEp.WebGpu, api, power, "x", true));

        Assert.Equal(2, options.Count);
        Assert.Equal(backend, options["dawnBackendType"]);
        Assert.Equal(powerValue, options["powerPreference"]);
    }

    [Fact]
    public void Build_NeverEmitsThePrefixedSpelling()
    {
        var options = WebGpuOptions.Build(GpuApi.Vulkan, GpuPreference.Performance);

        Assert.All(options.Keys, key => Assert.DoesNotContain("ep.webgpuexecutionprovider", key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_RejectsAnUnresolvedApi()
    {
        Assert.Throws<ArgumentException>(() => WebGpuOptions.Build(GpuApi.Auto, GpuPreference.Performance));
    }

    [Fact]
    public void Build_RejectsNonWebGpuPlans()
    {
        Assert.Throws<ArgumentException>(() => WebGpuOptions.Build(DevicePlan.OnCpu("cpu")));
    }
}
