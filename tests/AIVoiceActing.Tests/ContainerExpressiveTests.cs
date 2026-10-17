namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// How the container builds the expressive engine and the main one from the configuration: engine id,
/// precision and device come from the settings (nothing hard-coded to the CPU), Turbo may be the main
/// engine, and the render-cache key follows the engines' precision and device class.
/// </summary>
public sealed class ContainerExpressiveTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiva-cexp", Guid.NewGuid().ToString("N"));

    public ContainerExpressiveTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private void InstallTurboBase()
    {
        foreach (var asset in ModelCatalog.TurboRequiredAssets)
        {
            using var file = File.Create(Path.Combine(this.dir, asset.FileName));
            file.SetLength(asset.SizeBytes ?? 1024);
        }
    }

    private ServiceContainer Container(
        string primary = "kokoro",
        bool expressive = true,
        string? expressiveId = "chatterbox-turbo",
        Func<string, string>? precision = null,
        Func<string, string>? device = null,
        Func<DevicePreferences>? prefs = null) =>
        new(
            logSinkOverride: new FakeLogSink(),
            modelsDirFactory: () => this.dir,
            selectedEngineFactory: () => primary,
            expressiveEngineFactory: () => expressive,
            expressiveEngineIdFactory: () => expressiveId!,
            enginePrecisionFactory: precision,
            engineDeviceFactory: device,
            devicePreferencesFactory: prefs,
            isWineFactory: () => true,
            gpuMarkerPathFactory: () => Path.Combine(this.dir, "gpu-session.pending"),
            webGpuRuntimeFactory: () => new FakeWebGpuRuntime());

    [Theory]
    [InlineData("chatterbox-turbo", "chatterbox-turbo")]
    [InlineData("turbo", "chatterbox-turbo")] // alias
    [InlineData("f5", "chatterbox-turbo")] // retired
    [InlineData("kokoro", "chatterbox-turbo")] // does not clone a clip
    [InlineData("nonsense", "chatterbox-turbo")]
    [InlineData(null, "chatterbox-turbo")]
    public void TheExpressiveEngine_IsResolvedFromTheConfiguredId(string? configured, string expected)
    {
        using var container = this.Container(expressiveId: configured);

        Assert.Equal(expected, container.ExpressiveEngineId);
    }

    [Fact]
    public void Routing_IsActive_WhenOnAndInstalled_AndKokoroIsTheMainEngine()
    {
        this.InstallTurboBase();
        using var container = this.Container();

        Assert.True(container.ExpressiveRoutingActive);
        Assert.NotNull(container.ExpressiveSynthesizer);
    }

    [Fact]
    public void Routing_IsOff_WhenTurboIsTheMainEngine_BecauseEveryLineAlreadyGoesThroughIt()
    {
        this.InstallTurboBase();
        using var container = this.Container(primary: "chatterbox-turbo");

        Assert.False(container.ExpressiveRoutingActive);
        Assert.Null(container.ExpressiveSynthesizer);
        Assert.IsType<ChatterboxSynthesizer>(container.SpeechSynthesizer);
    }

    [Fact]
    public void Routing_IsOff_WhenSwitchedOff_OrNotInstalled()
    {
        using var notInstalled = this.Container();
        Assert.False(notInstalled.ExpressiveRoutingActive);

        this.InstallTurboBase();
        using var off = this.Container(expressive: false);
        Assert.False(off.ExpressiveRoutingActive);
    }

    [Fact]
    public void TheExpressiveEngine_FollowsTheDeviceSettings_NotAHardCodedCpu()
    {
        this.InstallTurboBase();
        using var container = this.Container(prefs: () => DevicePreferences.Default with { Mode = DeviceMode.Gpu });

        var status = container.ExpressiveSynthesizer!.Status;

        Assert.Equal(DeviceMode.Gpu, status.RequestedDevice);
    }

    [Fact]
    public void ThePerEngineDeviceOverride_BeatsTheGlobalMode()
    {
        this.InstallTurboBase();
        using var container = this.Container(
            device: id => id == "chatterbox-turbo" ? "cpu" : "inherit",
            prefs: () => DevicePreferences.Default with { Mode = DeviceMode.Gpu });

        var status = container.ExpressiveSynthesizer!.Status;

        Assert.Equal(DeviceMode.Cpu, status.RequestedDevice);
        Assert.Equal(DeviceEp.Cpu, status.ActiveDevice.Ep);
    }

    [Fact]
    public void ThePerEnginePrecisionSetting_ReachesTheEngine_AndShowsInTheRenderKey()
    {
        this.InstallTurboBase();
        var precision = "fp32";
        using var container = this.Container(
            primary: "chatterbox-turbo",
            precision: _ => precision,
            prefs: () => DevicePreferences.Default with { Mode = DeviceMode.Cpu });

        var fp32 = container.RenderEngineKey();
        precision = "q4f16"; // not downloaded here: falls back to full precision, so the key must not change
        var notInstalled = container.RenderEngineKey();

        Assert.Equal("chatterbox-turbo:fp32/cpu", fp32);
        Assert.Equal(fp32, notInstalled);
    }

    [Fact]
    public void TheRenderKey_FollowsTheDeviceMode_ForTheMainEngine()
    {
        this.InstallTurboBase();
        var mode = DeviceMode.Cpu;
        using var container = this.Container(primary: "chatterbox-turbo", prefs: () => DevicePreferences.Default with { Mode = mode });

        var cpu = container.RenderEngineKey();
        mode = DeviceMode.Gpu; // no verified model/probe here, so the plan stays on the CPU but wants the GPU: same audio class
        var wantedGpu = container.RenderEngineKey();

        Assert.Equal("chatterbox-turbo:fp32/cpu", cpu);
        Assert.Equal(cpu, wantedGpu);
    }

    [Fact]
    public void TheRenderKey_OfKokoro_IsJustTheEngineId_AndGainsTheExpressiveVariantWhileRoutingIsActive()
    {
        using var plain = this.Container(expressive: false);
        Assert.Equal("kokoro", plain.RenderEngineKey());

        this.InstallTurboBase();
        using var routed = this.Container();
        Assert.Equal("kokoro:x:fp32/cpu", routed.RenderEngineKey());
    }

    [Fact]
    public void RetiredEngines_FallBackToKokoro_ForTheMainEngine()
    {
        using var container = this.Container(primary: "f5", expressive: false);

        Assert.Equal("KokoroSynthesizer", container.SpeechSynthesizer.GetType().Name);
    }
}
