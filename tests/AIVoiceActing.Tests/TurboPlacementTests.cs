namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Microsoft.ML.OnnxRuntime;
using Xunit;

/// <summary>
/// Which file set Turbo loads for the precision setting and the device plan, and which sessions may
/// leave the CPU. Sessions are opened through the test seam as a tiny real graph, so no model downloads
/// or GPU are needed; the fake runtime records which sessions asked for the GPU provider.
/// </summary>
public sealed class TurboPlacementTests : IDisposable
{
    private static readonly DevicePlan GpuPlan = new(
        DeviceEp.WebGpu, GpuApi.Vulkan, GpuPreference.Performance, "Using the high-performance GPU through Vulkan.", WantedGpu: true);

    private readonly List<string> dirs = [];

    public void Dispose()
    {
        foreach (var dir in this.dirs.Where(Directory.Exists))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FakeTokenizer : ITextTokenizer
    {
        public int[] Encode(string text) => [1, 2, 3];
    }

    private sealed class Rig
    {
        public required ChatterboxSynthesizer Synth { get; init; }

        public required FakeDevicePlanner Planner { get; init; }

        public required FakeWebGpuRuntime Runtime { get; init; }

        public required GpuCrashGuard Guard { get; init; }

        public required List<string> Opened { get; init; }

        public required string Dir { get; init; }

        public string Marker => this.Guard.MarkerPath;
    }

    private Rig NewRig(
        string precision = "auto",
        bool base_ = true,
        bool q4f16 = false,
        bool tokenizerAndVoice = true,
        bool fp16 = false,
        Func<string, SessionOptions, InferenceSession>? open = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-placement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        this.dirs.Add(dir);
        if (tokenizerAndVoice)
        {
            Stub(dir, ModelCatalog.GroupAssets(ModelCatalog.TurboBaseGroup));
        }

        if (base_)
        {
            Stub(dir, ModelCatalog.GroupAssets(ModelCatalog.TurboGroup));
        }

        if (q4f16)
        {
            Stub(dir, ModelCatalog.GroupAssets(ModelCatalog.TurboQ4F16Group));
        }

        if (fp16)
        {
            Stub(dir, ModelCatalog.GroupAssets(ModelCatalog.TurboFp16Group));
        }

        var voice = Path.Combine(dir, "default.wav");
        WavCodec.WriteMono24k(voice, [0f]);
        var planner = new FakeDevicePlanner { Plan = GpuPlan };
        var runtime = new FakeWebGpuRuntime();
        var guard = new GpuCrashGuard(Path.Combine(dir, "gpu-session.pending"));
        var opened = new List<string>();
        var services = new TurboDeviceServices(
            planner,
            new OnnxSessionFactory(runtime, guard),
            LmGpuLoopReady: true,
            Precision: () => precision,
            OpenSession: open ?? ((path, options) =>
            {
                opened.Add(Path.GetFileName(path));
                return new InferenceSession(TestModels.Add, options);
            }));
        var synth = ChatterboxSynthesizer.CreateTurbo(
            dir, _ => voice, log: new FakeLogSink(), tokenizerFactory: () => new FakeTokenizer(), devices: services);
        return new Rig { Synth = synth, Planner = planner, Runtime = runtime, Guard = guard, Opened = opened, Dir = dir };
    }

    private static void Stub(string dir, IReadOnlyList<ModelAsset> assets)
    {
        foreach (var asset in assets)
        {
            using var file = File.Create(Path.Combine(dir, asset.FileName));
            file.SetLength(asset.SizeBytes ?? 1024);
        }
    }

    [Fact]
    public async Task Q4f16_OnAGpuPlan_OnlyTheLanguageModelAsksForTheGpu()
    {
        var rig = this.NewRig("q4f16", q4f16: true);
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal(
            [
                "turbo-speech-encoder-q4f16.onnx",
                "turbo-embed-tokens-q4f16.onnx",
                "turbo-language-model-q4f16.onnx",
                "turbo-conditional-decoder-q4f16.onnx",
            ],
            rig.Opened);
        Assert.Single(rig.Runtime.Appended); // the encoder, embedding and decoder never get the GPU provider
        Assert.True(File.Exists(rig.Marker), "the crash marker stays until the first line decoded");
        Assert.Equal(EngineState.Ready, synth.Status.State);
        Assert.Equal(DeviceEp.WebGpu, synth.Status.ActiveDevice.Ep);
        Assert.Equal("q4f16/gpu", synth.RenderVariant);
    }

    [Fact]
    public async Task Disposing_BeforeAnyLine_ClosesTheMarkerWithoutCondemningTheNextStart()
    {
        var rig = this.NewRig("q4f16", q4f16: true);
        var synth = rig.Synth;
        await synth.WarmUpAsync(CancellationToken.None);
        Assert.True(File.Exists(rig.Marker));

        synth.Dispose();

        Assert.False(File.Exists(rig.Marker));
    }

    [Fact]
    public async Task Auto_PrefersQ4f16_WhenItIsDownloadedAndTheGpuIsUsable()
    {
        var rig = this.NewRig("auto", q4f16: true);
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal("turbo-language-model-q4f16.onnx", rig.Opened[2]);
        Assert.Equal("q4f16", synth.EffectivePrecision);
    }

    [Fact]
    public async Task Auto_FallsToFp16_WhenOnlyFp16IsDownloaded()
    {
        var rig = this.NewRig("auto", fp16: true);
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal("turbo-language-model-fp16.onnx", rig.Opened[2]);
    }

    [Fact]
    public async Task Auto_OnACpuPlan_UsesTheFullPrecisionSet_AndNeverTouchesTheGpuProvider()
    {
        var rig = this.NewRig("auto", q4f16: true);
        using var synth = rig.Synth;
        rig.Planner.Plan = DevicePlan.OnCpu("Running on the CPU, as you chose.");

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal(
            [
                ModelCatalog.TurboSpeechEncoderFileName,
                ModelCatalog.TurboEmbedTokensFileName,
                ModelCatalog.TurboLanguageModelFileName,
                ModelCatalog.TurboConditionalDecoderFileName,
            ],
            rig.Opened);
        Assert.Empty(rig.Runtime.Appended);
        Assert.False(File.Exists(rig.Marker));
        Assert.Equal("fp32/cpu", synth.RenderVariant);
    }

    [Fact]
    public async Task Auto_WhenOnlyTheLargerSetHasAVerifiedGpuPlan_StillTakesTheGpu()
    {
        var rig = this.NewRig("auto", q4f16: true, fp16: true);
        using var synth = rig.Synth;
        rig.Planner.PlanByLm = lm => lm.Contains("fp16") && !lm.Contains("q4f16")
            ? GpuPlan
            : DevicePlan.OnCpu("The GPU is only used with voice models that have been tested with it; the installed one runs on the CPU.", wantedGpu: true);

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal("turbo-language-model-fp16.onnx", rig.Opened[2]);
    }

    [Fact]
    public async Task ASmallerPrecisionThatIsNotDownloaded_FallsBackToFullPrecision_AndSaysWhy()
    {
        var rig = this.NewRig("q4f16");
        using var synth = rig.Synth;
        rig.Planner.Plan = DevicePlan.OnCpu("Running on the CPU, as you chose.");

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal(ModelCatalog.TurboLanguageModelFileName, rig.Opened[2]);
        Assert.Equal("fp32", synth.EffectivePrecision);
    }

    [Theory]
    [InlineData(DeviceMode.Gpu)]
    [InlineData(DeviceMode.Auto)]
    public async Task Fp32_WithAGraphicsCardSetting_PlansLikeAutomatic_AndSaysSo(DeviceMode mode)
    {
        var rig = this.NewRig("fp32", q4f16: true);
        rig.Planner.Preferences = DevicePreferences.Default with { Mode = mode };
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal(ModelCatalog.TurboLanguageModelFileName.Replace(".onnx", "-q4f16.onnx"), rig.Opened[2]);
        Assert.Equal("q4f16", synth.EffectivePrecision);
        Assert.NotEmpty(rig.Runtime.Appended);
        Assert.Equal(EngineState.Ready, synth.Status.State);
        Assert.True(synth.Status.ActiveDevice.IsGpu);
        Assert.Equal(PrecisionNotes.FullSizeIgnoredOnGpu, synth.Status.Reason);
    }

    [Fact]
    public async Task Fp32_WithAGraphicsCardSetting_StillFallsBackWithTheRealReason_WhenTheGpuIsUnusable()
    {
        var rig = this.NewRig("fp32", q4f16: true);
        rig.Planner.Preferences = DevicePreferences.Default with { Mode = DeviceMode.Gpu };
        rig.Planner.Plan = DevicePlan.OnCpu("No compatible graphics card was found. Running on the CPU.", wantedGpu: true);
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal("fp32", synth.EffectivePrecision);
        Assert.Equal(EngineState.FellBack, synth.Status.State);
        Assert.Contains("No compatible graphics card", synth.Status.Reason);
    }

    [Fact]
    public async Task Fp32_WithTheProcessorSetting_AppliesAndRunsOnTheProcessor()
    {
        var rig = this.NewRig("fp32", q4f16: true);
        rig.Planner.Preferences = DevicePreferences.Default with { Mode = DeviceMode.Cpu };
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal(ModelCatalog.TurboLanguageModelFileName, rig.Opened[2]);
        Assert.Empty(rig.Runtime.Appended);
        Assert.Equal(EngineState.Ready, synth.Status.State);
        Assert.Equal(DeviceEp.Cpu, synth.Status.ActiveDevice.Ep);
    }

    [Fact]
    public async Task AModeOverride_FromARetry_DecidesWhetherTheFullSizePinApplies()
    {
        var rig = this.NewRig("fp32", q4f16: true);
        rig.Planner.Preferences = DevicePreferences.Default with { Mode = DeviceMode.Cpu };
        using var synth = rig.Synth;
        await synth.WarmUpAsync(CancellationToken.None);

        await synth.RetryAsync(DeviceMode.Gpu);

        Assert.Equal("q4f16", synth.EffectivePrecision);
    }

    [Fact]
    public async Task APlanIsAskedForTheLanguageModelOfTheChosenPrecision()
    {
        var rig = this.NewRig("fp16", fp16: true);
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Contains(rig.Planner.Calls, c => !c.Cheap && c.Lm == "turbo-language-model-fp16.onnx");
    }

    [Fact]
    public async Task AManagedFailureCreatingTheGpuSessions_RebuildsOnTheCpu_WithAReason_AndClosesTheMarker()
    {
        var failedOnce = false;
        var opened = new List<string>();
        var rig = this.NewRig("auto", q4f16: true, open: (path, options) =>
        {
            var name = Path.GetFileName(path);
            if (name.Contains("language-model-q4f16") && !failedOnce)
            {
                failedOnce = true;
                throw new InvalidOperationException("no adapter");
            }

            opened.Add(name);
            return new InferenceSession(TestModels.Add, options);
        });
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);

        var status = synth.Status;
        Assert.Equal(EngineState.FellBack, status.State);
        Assert.Equal(DeviceEp.Cpu, status.ActiveDevice.Ep);
        Assert.True(status.CanRetryGpu);
        Assert.Contains("CPU", status.Reason);
        Assert.False(File.Exists(rig.Marker));
        Assert.Contains(ModelCatalog.TurboLanguageModelFileName, opened); // auto goes back to full precision on the CPU
        Assert.Equal("fp32/cpu", synth.RenderVariant);
    }

    [Fact]
    public async Task WithoutTheGpuLoop_AGpuPlanIsBuiltOnTheCpu()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-placement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        this.dirs.Add(dir);
        Stub(dir, ModelCatalog.GroupAssets(ModelCatalog.TurboBaseGroup));
        Stub(dir, ModelCatalog.GroupAssets(ModelCatalog.TurboGroup));
        var runtime = new FakeWebGpuRuntime();
        var services = new TurboDeviceServices(
            new FakeDevicePlanner { Plan = GpuPlan },
            new OnnxSessionFactory(runtime, new GpuCrashGuard(Path.Combine(dir, "m"))),
            LmGpuLoopReady: false,
            OpenSession: (_, options) => new InferenceSession(TestModels.Add, options));
        using var synth = ChatterboxSynthesizer.CreateTurbo(dir, _ => null, log: new FakeLogSink(), devices: services);

        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Empty(runtime.Appended);
        Assert.Equal("fp32/cpu", synth.RenderVariant);
    }

    [Fact]
    public void RenderVariant_DiffersByPrecisionAndDeviceClass_ButNotByAdapter()
    {
        var gpuQ4 = this.NewRig("q4f16", q4f16: true);
        var cpuQ4 = this.NewRig("q4f16", q4f16: true);
        cpuQ4.Planner.Plan = DevicePlan.OnCpu("Running on the CPU, as you chose.");
        var gpuFp16 = this.NewRig("fp16", fp16: true);
        var lowPower = this.NewRig("q4f16", q4f16: true);
        lowPower.Planner.Plan = GpuPlan with { PowerPreference = GpuPreference.LowPower };
        using var a = gpuQ4.Synth;
        using var b = cpuQ4.Synth;
        using var c = gpuFp16.Synth;
        using var d = lowPower.Synth;

        Assert.Equal("q4f16/gpu", a.RenderVariant);
        Assert.Equal("q4f16/cpu", b.RenderVariant);
        Assert.Equal("fp16/gpu", c.RenderVariant);
        Assert.Equal(a.RenderVariant, d.RenderVariant);
        Assert.Equal(4, new[] { a.RenderVariant, b.RenderVariant, c.RenderVariant }.Append("fp32/cpu").Distinct().Count());
    }

    // ---- the small graphics card install: base files + q4f16 set, no full-size files ---------------------

    [Fact]
    public async Task AGpuInstall_WithoutTheFullSizeFiles_IsReady_AndRunsTheQ4f16SetOnTheGpu()
    {
        var rig = this.NewRig("auto", base_: false, q4f16: true);
        using var synth = rig.Synth;

        Assert.True(synth.IsReady);
        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal("q4f16/gpu", synth.RenderVariant);
        Assert.Equal(
            [
                "turbo-speech-encoder-q4f16.onnx",
                "turbo-embed-tokens-q4f16.onnx",
                "turbo-language-model-q4f16.onnx",
                "turbo-conditional-decoder-q4f16.onnx",
            ],
            rig.Opened);
        Assert.False(File.Exists(Path.Combine(rig.Dir, ModelCatalog.TurboLanguageModelFileName)));
    }

    [Fact]
    public async Task AGpuInstall_WhenTheGpuIsNotUsed_StillSpeaksFromTheQ4f16SetOnTheCpu()
    {
        // The crash guard switched the graphics card off; the full-size files were never downloaded.
        var rig = this.NewRig("auto", base_: false, q4f16: true);
        using var synth = rig.Synth;
        rig.Planner.Plan = DevicePlan.OnCpu("The graphics card was switched off after a crash.", wantedGpu: true);

        Assert.True(synth.IsReady);
        await synth.WarmUpAsync(CancellationToken.None);

        Assert.Equal("q4f16/cpu", synth.RenderVariant);
        Assert.Empty(rig.Runtime.Appended);
        Assert.Equal(EngineState.FellBack, synth.Status.State);
    }

    [Fact]
    public void OnlyTheBaseFiles_IsNotReady_AndTheHintNamesTheSmallSetOnAGpuMachine()
    {
        var rig = this.NewRig("auto", base_: false);
        using var synth = rig.Synth;
        rig.Planner.Preferences = DevicePreferences.Default with { Mode = DeviceMode.Gpu };

        Assert.False(synth.IsReady);
        Assert.Contains("q4f16", synth.NotReadyReason);
        Assert.Contains("Models tab", synth.NotReadyReason);
    }

    [Fact]
    public void OnlyTheBaseFiles_OnACpuMachine_AsksForTheFullSizeSet()
    {
        var rig = this.NewRig("auto", base_: false);
        using var synth = rig.Synth;
        rig.Planner.Preferences = DevicePreferences.Default with { Mode = DeviceMode.Cpu };

        Assert.False(synth.IsReady);
        Assert.Contains(ModelCatalog.TurboSpeechEncoderFileName, synth.NotReadyReason);
    }

    [Fact]
    public void FullSizeFilesWithoutTheTokenizerAndVoice_AreNotEnough()
    {
        var rig = this.NewRig("auto", tokenizerAndVoice: false);
        using var synth = rig.Synth;

        Assert.False(synth.IsReady);
        Assert.Contains(ModelCatalog.TurboTokenizerJsonFileName, synth.NotReadyReason);
    }
}
