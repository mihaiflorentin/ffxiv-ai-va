namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

/// <summary>Runs only with AIVA_GPU_TESTS=1 on a machine with a Vulkan (or D3D12) GPU; CI skips it.</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AIVA_GPU_TESTS") != "1")
        {
            this.Skip = "Set AIVA_GPU_TESTS=1 to run the GPU integration tests.";
        }
    }
}

/// <summary>
/// Needs AIVA_GPU_TESTS=1 and AIVA_TURBO_MODELS=&lt;folder with the Turbo base files and the q4f16 set&gt;
/// (local file names as the plugin's Models tab writes them): the small graphics card install. The one test that
/// runs the full-size fp32 files on the CPU uses <see cref="GpuTurboFp32FactAttribute"/> and skips without them.
/// </summary>
public sealed class GpuTurboFactAttribute : FactAttribute
{
    public GpuTurboFactAttribute()
    {
        var models = Environment.GetEnvironmentVariable("AIVA_TURBO_MODELS");
        if (Environment.GetEnvironmentVariable("AIVA_GPU_TESTS") != "1")
        {
            this.Skip = "Set AIVA_GPU_TESTS=1 to run the GPU integration tests.";
        }
        else if (string.IsNullOrWhiteSpace(models) || !TurboFiles.For(TurboPrecision.Q4F16).IsInstalled(models))
        {
            this.Skip = "Set AIVA_TURBO_MODELS to a folder holding the Turbo base set and the q4f16 set.";
        }
    }
}

/// <summary>As <see cref="GpuTurboFactAttribute"/>, and the folder also holds the full-size fp32 files.</summary>
public sealed class GpuTurboFp32FactAttribute : FactAttribute
{
    public GpuTurboFp32FactAttribute()
    {
        var models = Environment.GetEnvironmentVariable("AIVA_TURBO_MODELS");
        if (Environment.GetEnvironmentVariable("AIVA_GPU_TESTS") != "1")
        {
            this.Skip = "Set AIVA_GPU_TESTS=1 to run the GPU integration tests.";
        }
        else if (string.IsNullOrWhiteSpace(models) || !TurboFiles.For(TurboPrecision.Fp32).IsInstalled(models))
        {
            this.Skip = "Set AIVA_TURBO_MODELS to a folder that also holds the full-size (fp32) Turbo files.";
        }
    }
}

public sealed class GpuIntegrationTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiva-gpu", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private static string LibraryPath()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";
        return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", WebGpuRuntime.LibraryFileName);
    }

    [GpuFact]
    public void TheRuntimeRegisters_AndListsAtLeastOneGpu()
    {
        var runtime = new WebGpuRuntime(null, isWine: false, libraryPathOverride: LibraryPath());

        var listing = runtime.Enumerate();

        Assert.True(listing.Available, listing.Failure);
        Assert.NotEmpty(listing.Adapters);
    }

    [GpuFact]
    public void AToyGraph_RunsOnTheGpu_ThroughTheSessionFactory_AndTheMarkerClearsAfterTheFirstRun()
    {
        var runtime = new WebGpuRuntime(null, isWine: false, libraryPathOverride: LibraryPath());
        var marker = Path.Combine(this.dir, "gpu-session.pending");
        var factory = new OnnxSessionFactory(runtime, new GpuCrashGuard(marker));
        var api = OperatingSystem.IsWindows() ? GpuApi.D3D12 : GpuApi.Vulkan;
        var plan = new DevicePlan(DeviceEp.WebGpu, api, GpuPreference.Performance, "test", true);

        var build = factory.Create(SessionRole.Lm, plan);
        using var options = build.Options;
        Assert.Equal(DeviceEp.WebGpu, build.Ep);
        Assert.True(File.Exists(marker));

        using var session = new InferenceSession(TestModels.Add, options);
        var a = new DenseTensor<float>(new float[] { 1, 2, 3, 4 }, [1, 4]);
        var b = new DenseTensor<float>(new float[] { 10, 20, 30, 40 }, [1, 4]);
        using var result = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("a", a),
            NamedOnnxValue.CreateFromTensor("b", b),
        ]);
        build.Guard!.Succeeded();

        Assert.Equal(new float[] { 11, 22, 33, 44 }, result[0].AsTensor<float>().ToArray());
        Assert.False(File.Exists(marker));
    }

    private sealed class FixedTokenizer : ITextTokenizer
    {
        // "The Warriors of Light have returned from the frozen north at last." (GPT-2 style ids; any valid text ids will do for a CPU/GPU comparison)
        public int[] Encode(string text) => [464, 21386, 286, 4401, 423, 4504, 422, 262, 8556, 8210, 379, 938, 13];
    }

    private ChatterboxSynthesizer NewTurbo(string models, DeviceMode mode, string precision, string tag, out GpuCrashGuard guard)
    {
        var runtime = new WebGpuRuntime(null, isWine: false, libraryPathOverride: LibraryPath());
        var probe = new DeviceProbe(runtime);
        probe.Detect();
        guard = new GpuCrashGuard(Path.Combine(this.dir, $"marker-{tag}.pending"));
        var prefs = DevicePreferences.Default with { Mode = mode };
        var services = new TurboDeviceServices(
            new DevicePlanner(() => prefs, probe),
            new OnnxSessionFactory(runtime, guard),
            LmGpuLoopReady: true,
            Precision: () => precision);
        var voice = Path.Combine(models, ModelCatalog.TurboDefaultVoiceFileName);
        return ChatterboxSynthesizer.CreateTurbo(
            models, _ => voice, devices: services, tokenizerFactory: () => new FixedTokenizer());
    }

    [GpuTurboFact]
    public async Task Turbo_Q4f16_OnTheGpu_StartsLikeTheCpuStream_AndEndsAboutAsLong()
    {
        var models = Environment.GetEnvironmentVariable("AIVA_TURBO_MODELS")!;
        Directory.CreateDirectory(this.dir);
        var request = new SynthesisRequest("default", "The Warriors of Light have returned.", 0.5f, []);

        using var gpu = this.NewTurbo(models, DeviceMode.Gpu, "q4f16", "gpu", out var gpuGuard);
        var gpuAudio = await gpu.SynthesizeAsync(request, CancellationToken.None);
        var gpuTokens = gpu.LastSpeechTokens.ToArray();

        Assert.Equal(DeviceEp.WebGpu, gpu.Status.ActiveDevice.Ep);
        Assert.Equal("q4f16/gpu", gpu.RenderVariant);
        Assert.False(File.Exists(gpuGuard.MarkerPath), "the first decoded line must close the crash marker");

        using var cpu = this.NewTurbo(models, DeviceMode.Cpu, "q4f16", "cpu", out _);
        var cpuAudio = await cpu.SynthesizeAsync(request, CancellationToken.None);

        Assert.Equal("q4f16/cpu", cpu.RenderVariant);
        Assert.NotEmpty(gpuTokens);

        // Greedy decoding on a different device can flip a near-tie (float math differs in the last bits) and
        // continue differently from there; the streams must start the same and be about as long.
        var cpuTokens = cpu.LastSpeechTokens.ToArray();
        var common = cpuTokens.Zip(gpuTokens).TakeWhile(pair => pair.First == pair.Second).Count();
        Assert.True(common >= Math.Min(cpuTokens.Length, gpuTokens.Length) / 2, $"streams diverge at token {common} of {cpuTokens.Length}/{gpuTokens.Length}");
        Assert.InRange(gpuTokens.Length, cpuTokens.Length * 3 / 4, cpuTokens.Length * 5 / 4);
        Assert.True(gpuAudio.Samples.Length > 24000);
        Assert.True(cpuAudio.Samples.Length > 24000);
    }

    [GpuTurboFp32Fact]
    public async Task Turbo_OnTheCpu_RunsTheSameLoop_WithFullPrecision()
    {
        var models = Environment.GetEnvironmentVariable("AIVA_TURBO_MODELS")!;
        Directory.CreateDirectory(this.dir);

        using var cpu = this.NewTurbo(models, DeviceMode.Cpu, "fp32", "fp32", out _);
        var audio = await cpu.SynthesizeAsync(new SynthesisRequest("default", "Hello there.", 0.5f, []), CancellationToken.None);

        Assert.Equal("fp32/cpu", cpu.RenderVariant);
        Assert.NotEmpty(cpu.LastSpeechTokens);
        Assert.True(audio.Samples.Length > 12000);
    }

    [GpuTurboFact]
    public async Task Turbo_Q4f16_Streamed_OnTheGpu_IsTheSameLine_InSeveralPieces()
    {
        var models = Environment.GetEnvironmentVariable("AIVA_TURBO_MODELS")!;
        Directory.CreateDirectory(this.dir);
        var request = new SynthesisRequest("default", "The Warriors of Light have returned.", 0.5f, []);

        using var gpu = this.NewTurbo(models, DeviceMode.Gpu, "q4f16", "stream", out var guard);
        var whole = await gpu.SynthesizeAsync(request, CancellationToken.None);
        var wholeTokens = gpu.LastSpeechTokens.ToArray();
        Assert.True(gpu.SupportsStreaming); // the language model is on the GPU

        var pieces = new List<float[]>();
        var streamed = await gpu.SynthesizeStreamingAsync(request, samples => pieces.Add(samples.ToArray()), CancellationToken.None);

        Assert.Equal(wholeTokens, gpu.LastSpeechTokens.ToArray()); // same greedy tokens, chunking only changes the decoder
        Assert.Equal(whole.Samples.Length, streamed.Samples.Length);
        Assert.Equal(streamed.Samples, pieces.SelectMany(p => p).ToArray());
        Assert.True(pieces.Count >= 2, $"expected several pieces, got {pieces.Count}");
        Assert.False(File.Exists(guard.MarkerPath));
        var stats = gpu.LastStreamingStats!;
        Assert.Equal(0, stats.LengthMismatches);
        Assert.InRange(stats.FirstAudioMs, 0, stats.TotalMs);

        // Same loudness: no seam swallows or doubles a stretch of speech.
        var ratio = AudioPolish.Rms(streamed.Samples) / AudioPolish.Rms(whole.Samples);
        Assert.InRange(ratio, 0.8, 1.25);
    }
}
