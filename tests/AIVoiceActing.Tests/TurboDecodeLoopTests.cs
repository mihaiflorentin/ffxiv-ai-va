namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// The real decode loop (OrtValue / IoBinding, key-value cache fed back every step) on tiny stand-in
/// graphs: their argmax token depends on how long the cache is, so a loop that fails to thread the cache
/// through produces a different token stream. Covers both cache element types, the guard scope, and the
/// managed-failure fallback to the CPU. No model downloads, no GPU.
/// </summary>
public sealed class TurboDecodeLoopTests : IDisposable
{
    private static readonly DevicePlan GpuPlan = new(
        DeviceEp.WebGpu, GpuApi.Vulkan, GpuPreference.Performance, "Using the high-performance GPU through Vulkan.", WantedGpu: true);

    // The stand-in LM emits (total_length % 97) + 100 where total starts at 4 condition frames + 3 text tokens
    // and grows by one per step, and STOP at total 47: forty speech tokens, 107 to 146.
    private static readonly long[] ExpectedTokens = [.. Enumerable.Range(107, 40).Select(i => (long)i)];

    private readonly List<string> dirs = [];

    public void Dispose()
    {
        foreach (var dir in this.dirs.Where(Directory.Exists))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class ThreeTokens : ITextTokenizer
    {
        public int[] Encode(string text) => [1, 2, 3];
    }

    private sealed class Rig
    {
        public required ChatterboxSynthesizer Synth { get; init; }

        public required FakeDevicePlanner Planner { get; init; }

        public required FakeWebGpuRuntime Runtime { get; init; }

        public required GpuCrashGuard Guard { get; init; }

        public required int[] ClearFlagCalls { get; init; }

        public string Voice { get; init; } = string.Empty;
    }

    /// <summary>Writes the stand-in files under the catalog's local names, padded to the catalog sizes so the installed-checks pass.</summary>
    private string Install(byte[] lm32, byte[] lm16, bool smallSets = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-tiny-turbo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        this.dirs.Add(dir);
        foreach (var group in new[] { ModelCatalog.TurboGroup, ModelCatalog.TurboFp16Group, ModelCatalog.TurboQ4F16Group })
        {
            if (!smallSets && group != ModelCatalog.TurboGroup)
            {
                continue;
            }

            foreach (var asset in ModelCatalog.GroupAssets(group))
            {
                var path = Path.Combine(dir, asset.FileName);
                var size = asset.SizeBytes ?? 1024;
                byte[]? model = asset.FileName switch
                {
                    var n when n.Contains("speech-encoder") && n.EndsWith(".onnx") => TinyTurboModels.Encoder,
                    var n when n.Contains("embed-tokens") && n.EndsWith(".onnx") => TinyTurboModels.Embed,
                    var n when n.Contains("conditional-decoder") && n.EndsWith(".onnx") => TinyTurboModels.Decoder,
                    var n when n.Contains("language-model") && n.EndsWith(".onnx") => group == ModelCatalog.TurboGroup ? lm32 : lm16,
                    _ => null,
                };
                if (model is null)
                {
                    using var stub = File.Create(path);
                    stub.SetLength(size);
                }
                else
                {
                    File.WriteAllBytes(path, PadTo(model, size));
                }
            }
        }

        return dir;
    }

    /// <summary>Appends an unknown protobuf field (number 100, length-delimited), which the parser skips, so the file is exactly <paramref name="size"/> bytes.</summary>
    private static byte[] PadTo(byte[] model, long size)
    {
        var extra = size - model.Length;
        Assert.True(extra >= 8, $"stand-in model ({model.Length} bytes) does not fit the catalog size {size}");
        for (var lengthBytes = 1; lengthBytes <= 5; lengthBytes++)
        {
            var payload = extra - 2 - lengthBytes; // tag (2 bytes) + length varint + payload
            if (payload >= 0 && VarintLength(payload) == lengthBytes)
            {
                var padded = new List<byte>(model) { 0xA2, 0x06 };
                for (var value = (ulong)payload; ; value >>= 7)
                {
                    var next = (byte)(value & 0x7F);
                    if (value >> 7 != 0)
                    {
                        padded.Add((byte)(next | 0x80));
                    }
                    else
                    {
                        padded.Add(next);
                        break;
                    }
                }

                padded.AddRange(new byte[payload]);
                Assert.Equal(size, padded.Count);
                return [.. padded];
            }
        }

        throw new InvalidOperationException("cannot pad");
    }

    private static int VarintLength(long value)
    {
        var length = 1;
        for (var v = (ulong)value; v >= 0x80; v >>= 7)
        {
            length++;
        }

        return length;
    }

    private Rig NewRig(string dir, string precision, Func<string, DevicePlan>? planByLm = null, DevicePlan? plan = null)
    {
        var voice = Path.Combine(dir, "voice.wav");
        WavCodec.WriteMono24k(voice, new float[2400]);
        var planner = new FakeDevicePlanner { Plan = plan ?? DevicePlan.OnCpu("Running on the CPU, as you chose."), PlanByLm = planByLm };
        var runtime = new FakeWebGpuRuntime();
        var guard = new GpuCrashGuard(Path.Combine(dir, "gpu-session.pending"));
        var cleared = new int[1];
        var services = new TurboDeviceServices(
            planner,
            new OnnxSessionFactory(runtime, guard),
            ClearCrashFlag: () => cleared[0]++,
            LmGpuLoopReady: true,
            Precision: () => precision);
        var synth = ChatterboxSynthesizer.CreateTurbo(
            dir, _ => voice, log: new FakeLogSink(), tokenizerFactory: () => new ThreeTokens(), devices: services);
        return new Rig { Synth = synth, Planner = planner, Runtime = runtime, Guard = guard, ClearFlagCalls = cleared, Voice = voice };
    }

    private static SynthesisRequest Line() => new("default", "Hello there.", 0.5f, []);

    [Fact]
    public async Task OnTheCpu_Fp32_TheCacheIsFedBackEveryStep()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        var rig = this.NewRig(dir, "fp32");
        using var synth = rig.Synth;

        var audio = await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Equal(5 + 40 + 3, audio.Samples.Length); // prompt tokens + speech tokens + three silence tokens
        Assert.Equal("fp32/cpu", synth.RenderVariant);
    }

    [Fact]
    public async Task OnTheCpu_Float16Cache_ForTheSmallerPrecisions()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        var rig = this.NewRig(dir, "q4f16");
        using var synth = rig.Synth;

        await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Equal("q4f16/cpu", synth.RenderVariant);
    }

    [Fact]
    public async Task OnAGpuPlan_TheSameLoopRuns_AndTheFirstFinishedLineClosesTheCrashMarker()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        var rig = this.NewRig(dir, "auto", plan: GpuPlan);
        using var synth = rig.Synth;
        await synth.WarmUpAsync(CancellationToken.None);
        Assert.True(File.Exists(rig.Guard.MarkerPath));

        await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.False(File.Exists(rig.Guard.MarkerPath));
        Assert.Equal("q4f16/gpu", synth.RenderVariant);
        Assert.Equal(EngineState.Ready, synth.Status.State);
    }

    [Fact]
    public async Task AManagedFailureOnTheGpu_FallsBackToTheCpu_FinishesTheLine_AndOffersRetry()
    {
        // The small-precision language model throws when it runs; the full-precision one is fine.
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.LmBad16);
        var rig = this.NewRig(dir, "auto", planByLm: lm => lm.Contains("q4f16") ? GpuPlan : DevicePlan.OnCpu("Running on the CPU.", wantedGpu: true));
        using var synth = rig.Synth;

        var audio = await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Equal(48, audio.Samples.Length);
        var status = synth.Status;
        Assert.Equal(EngineState.FellBack, status.State);
        Assert.Equal(DeviceEp.Cpu, status.ActiveDevice.Ep);
        Assert.True(status.CanRetryGpu);
        Assert.Contains("GPU", status.Reason);
        Assert.False(File.Exists(rig.Guard.MarkerPath), "a managed failure closes the scope as 'failed safely'");
        Assert.Equal("fp32/cpu", synth.RenderVariant);

        // The next line stays on the CPU without trying the GPU again.
        var appendsBefore = rig.Runtime.Appended.Count;
        await synth.SynthesizeAsync(Line(), CancellationToken.None);
        Assert.Equal(appendsBefore, rig.Runtime.Appended.Count);

        // Retry on the GPU clears the saved flag and tries the GPU again.
        await synth.RetryAsync(DeviceMode.Gpu);
        Assert.Equal(1, rig.ClearFlagCalls[0]);
        Assert.Equal(appendsBefore + 1, rig.Runtime.Appended.Count);
    }

    [Fact]
    public async Task AnUnreadableReferenceClip_IsNotMistakenForAGpuFailure()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        var rig = this.NewRig(dir, "auto", plan: GpuPlan);
        using var synth = rig.Synth;

        await synth.WarmUpAsync(CancellationToken.None);
        File.Delete(rig.Voice);

        await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Line(), CancellationToken.None));

        Assert.Equal("q4f16/gpu", synth.RenderVariant); // still the GPU engine: no fallback happened
        Assert.True(File.Exists(rig.Guard.MarkerPath), "no GPU run finished, and none failed");
    }
}
