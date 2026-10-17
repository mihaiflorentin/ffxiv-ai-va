namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Xunit;

public sealed class WineSupportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aiva-wine", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void DetectWine_LooksForTheNtdllExport()
    {
        (string Module, string Export)? asked = null;

        var wine = RuntimeHost.DetectWine((module, export) =>
        {
            asked = (module, export);
            return true;
        });

        Assert.True(wine);
        Assert.Equal(("ntdll.dll", "wine_get_version"), asked);
        Assert.False(RuntimeHost.DetectWine((_, _) => false));
    }

    [Fact]
    public void DetectWine_NeverThrows()
    {
        Assert.False(RuntimeHost.DetectWine((_, _) => throw new DllNotFoundException()));
    }

    [Fact]
    public void PlatformOf_WineWinsOverWindows()
    {
        Assert.Equal(DevicePlatform.Wine, RuntimeHost.PlatformOf(isWine: true));
    }

    private (string Plugin, string System) Dirs()
    {
        var plugin = Path.Combine(this.root, "plugin");
        var system = Path.Combine(this.root, "system32");
        Directory.CreateDirectory(plugin);
        Directory.CreateDirectory(system);
        return (plugin, system);
    }

    [Fact]
    public void VulkanLoader_IsCopiedNextToTheRuntime()
    {
        var (plugin, system) = this.Dirs();
        File.WriteAllBytes(Path.Combine(system, "vulkan-1.dll"), [1, 2, 3, 4]);

        Assert.Null(WineVulkanShim.EnsureLoader(plugin, system));

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(plugin, "vulkan-1.dll")));
    }

    [Fact]
    public void VulkanLoader_IsLeftAloneWhenAlreadyPresent()
    {
        var (plugin, system) = this.Dirs();
        File.WriteAllBytes(Path.Combine(system, "vulkan-1.dll"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(plugin, "vulkan-1.dll"), [9, 9, 9, 9]);

        Assert.Null(WineVulkanShim.EnsureLoader(plugin, system));

        Assert.Equal([9, 9, 9, 9], File.ReadAllBytes(Path.Combine(plugin, "vulkan-1.dll")));
    }

    [Fact]
    public void VulkanLoader_IsReplacedWhenTheSizeDiffers()
    {
        var (plugin, system) = this.Dirs();
        File.WriteAllBytes(Path.Combine(system, "vulkan-1.dll"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(plugin, "vulkan-1.dll"), [9]);

        Assert.Null(WineVulkanShim.EnsureLoader(plugin, system));

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(plugin, "vulkan-1.dll")));
    }

    [Fact]
    public void MissingSystemLoader_IsReportedInPlainText_AndNothingIsWritten()
    {
        var (plugin, system) = this.Dirs();

        var problem = WineVulkanShim.EnsureLoader(plugin, system);

        Assert.NotNull(problem);
        Assert.Contains("Vulkan", problem);
        Assert.False(File.Exists(Path.Combine(plugin, "vulkan-1.dll")));
    }

    [Fact]
    public void UnwritablePluginFolder_IsReportedNotThrown()
    {
        var (_, system) = this.Dirs();
        File.WriteAllBytes(Path.Combine(system, "vulkan-1.dll"), [1]);

        var problem = WineVulkanShim.EnsureLoader(Path.Combine(this.root, "no", "such", "dir"), system);

        Assert.NotNull(problem);
    }
}
