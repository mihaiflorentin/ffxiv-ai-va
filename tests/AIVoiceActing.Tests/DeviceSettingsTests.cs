namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using Newtonsoft.Json;
using Xunit;

public sealed class DeviceSettingsTests
{
    [Fact]
    public void Defaults_MapToAutoPerformance()
    {
        var prefs = new DeviceSettings().ToPreferences();

        Assert.Equal(DevicePreferences.Default, prefs);
    }

    [Theory]
    [InlineData("auto", DeviceMode.Auto)]
    [InlineData("gpu", DeviceMode.Gpu)]
    [InlineData("cpu", DeviceMode.Cpu)]
    [InlineData("garbage", DeviceMode.Auto)]
    public void Mode_Maps(string value, DeviceMode expected)
    {
        Assert.Equal(expected, new DeviceSettings { Mode = value }.ToPreferences().Mode);
    }

    [Theory]
    [InlineData("performance", GpuPreference.Performance)]
    [InlineData("lowpower", GpuPreference.LowPower)]
    [InlineData("", GpuPreference.Performance)]
    public void GpuPreference_Maps(string value, GpuPreference expected)
    {
        Assert.Equal(expected, new DeviceSettings { GpuPreference = value }.ToPreferences().Preference);
    }

    [Theory]
    [InlineData("auto", GpuApi.Auto)]
    [InlineData("vulkan", GpuApi.Vulkan)]
    [InlineData("d3d12", GpuApi.D3D12)]
    public void Api_Maps(string value, GpuApi expected)
    {
        Assert.Equal(expected, new DeviceSettings { Api = value }.ToPreferences().Api);
    }

    [Theory]
    [InlineData("auto", DeviceBackend.Auto)]
    [InlineData("webgpu", DeviceBackend.WebGpu)]
    [InlineData("directml", DeviceBackend.DirectML)]
    public void Backend_Maps(string value, DeviceBackend expected)
    {
        Assert.Equal(expected, new DeviceSettings { Backend = value }.ToPreferences().Backend);
    }

    [Fact]
    public void CrashGuardFlag_IsCarried()
    {
        Assert.True(new DeviceSettings { GpuDisabledByCrashGuard = true }.ToPreferences().GpuDisabledByCrashGuard);
    }

    [Fact]
    public void AFileThatStillCarriesTheOldAdapterId_LoadsAndKeepsTheDefaultPreference()
    {
        const string json = "{\"Mode\":\"gpu\",\"AdapterId\":\"abc123\",\"Backend\":\"auto\"}";

        var settings = JsonConvert.DeserializeObject<DeviceSettings>(json)!;

        Assert.Equal("gpu", settings.Mode);
        Assert.Equal("performance", settings.GpuPreference);
    }

    [Fact]
    public void GpuPreference_RoundTripsThroughNewtonsoft()
    {
        var json = JsonConvert.SerializeObject(new DeviceSettings { GpuPreference = DeviceSettings.PreferenceLowPower });

        Assert.Equal("lowpower", JsonConvert.DeserializeObject<DeviceSettings>(json)!.GpuPreference);
    }
}
