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
    private string Install(byte[] lm32, byte[] lm16, bool smallSets = true, byte[]? decoder = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-tiny-turbo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        this.dirs.Add(dir);
        foreach (var group in new[] { ModelCatalog.TurboBaseGroup, ModelCatalog.TurboGroup, ModelCatalog.TurboFp16Group, ModelCatalog.TurboQ4F16Group })
        {
            if (!smallSets && group is ModelCatalog.TurboFp16Group or ModelCatalog.TurboQ4F16Group)
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
                    var n when n.Contains("conditional-decoder") && n.EndsWith(".onnx") => decoder ?? TinyTurboModels.Decoder,
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

    // ---- chunked (streaming) decode ---------------------------------------------------------------------

    // Chunks of 8 tokens with 2 tokens of context: the 43-token stand-in line always splits, whatever the thread timing.
    private static readonly StreamingOptions TinyChunks = new(new ChunkScheduleOptions(8, 1.0, 8, 4, 2, 2), 60, 480);

    private const int Spt = 960;

    private static async Task<(SynthesisResult Whole, List<float[]> Pieces)> Stream(
        ChatterboxSynthesizer synth, CancellationToken ct = default, Action<int>? afterPiece = null)
    {
        var pieces = new List<float[]>();
        var whole = await synth.SynthesizeStreamingAsync(
            Line(),
            samples =>
            {
                pieces.Add(samples.ToArray());
                afterPiece?.Invoke(pieces.Count);
            },
            ct);
        return (whole, pieces);
    }

    private ChatterboxSynthesizer StreamingSynth(string precision = "fp32", DevicePlan? plan = null, byte[]? lm16 = null, Func<string, DevicePlan>? planByLm = null)
    {
        var dir = this.Install(TinyTurboModels.Lm32, lm16 ?? TinyTurboModels.Lm16, decoder: TinyTurboModels.StreamDecoder);
        var synth = this.NewRig(dir, precision, planByLm, plan).Synth;
        synth.StreamingOverride = true;
        synth.Streaming = TinyChunks;
        return synth;
    }

    [Fact]
    public async Task TheStreamedLine_MatchesTheWholeDecode_ExceptInsideTheCrossFades()
    {
        using var synth = this.StreamingSynth();
        var reference = await synth.SynthesizeAsync(Line(), CancellationToken.None); // one decoder call, no chunks
        Assert.Equal(43 * Spt, reference.Samples.Length);

        var (whole, pieces) = await Stream(synth);

        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Equal(reference.Samples.Length, whole.Samples.Length);
        Assert.Equal(whole.Samples, pieces.SelectMany(p => p).ToArray()); // what was handed out is what is returned
        Assert.True(pieces.Count >= 5, $"expected the line in several pieces, got {pieces.Count}");
        for (var i = 0; i < reference.Samples.Length; i++)
        {
            var offset = i % (8 * Spt);
            var inFade = offset < 480 || offset >= (8 * Spt) - 480;
            if (!inFade)
            {
                Assert.Equal(reference.Samples[i], whole.Samples[i]);
            }
            else
            {
                Assert.InRange(whole.Samples[i], reference.Samples[i] - 1e-6f, (reference.Samples[i] * 1.415f) + 1e-6f);
            }
        }

        var stats = synth.LastStreamingStats!;
        Assert.Equal(0, stats.LengthMismatches);
        Assert.Equal(40, stats.SpeechTokens);
        Assert.True(stats.Chunks >= 5);
        Assert.InRange(stats.FirstAudioMs, 0, stats.TotalMs);
    }

    [Fact]
    public async Task ACallerWhoStopsListening_CancelsTheLineMidStream_AndTheEngineStaysUsable()
    {
        using var synth = this.StreamingSynth();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Stream(synth, cts.Token, afterPiece: count =>
            {
                if (count == 2)
                {
                    cts.Cancel();
                }
            }));

        // The language model worker has stopped and the synthesis gate is free again.
        var (whole, _) = await Stream(synth);
        Assert.Equal(43 * Spt, whole.Samples.Length);
        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
    }

    [Fact]
    public async Task ACancelledTokenBeforeStarting_ProducesNothing()
    {
        using var synth = this.StreamingSynth();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var heard = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => synth.SynthesizeStreamingAsync(Line(), _ => heard++, cts.Token));

        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task OnTheGpu_StreamingIsOffered_OnTheCpu_ItIsNot_UnlessForced()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16, decoder: TinyTurboModels.StreamDecoder);
        using var gpu = this.NewRig(dir, "auto", plan: GpuPlan).Synth;
        Assert.False(gpu.SupportsStreaming); // not loaded yet: nothing to say
        await gpu.WarmUpAsync(CancellationToken.None);
        Assert.True(gpu.SupportsStreaming);

        using var cpu = this.NewRig(this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16), "fp32").Synth;
        await cpu.WarmUpAsync(CancellationToken.None);
        Assert.False(cpu.SupportsStreaming);
        cpu.StreamingOverride = true;
        Assert.True(cpu.SupportsStreaming);
        Assert.Equal(24000, cpu.StreamingSampleRate);
    }

    [Fact]
    public async Task AStreamedLineOnTheGpu_ClosesTheCrashMarker_LikeAWholeLine()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16, decoder: TinyTurboModels.StreamDecoder);
        var rig = this.NewRig(dir, "auto", plan: GpuPlan);
        using var synth = rig.Synth;
        synth.Streaming = TinyChunks;
        await synth.WarmUpAsync(CancellationToken.None);
        Assert.True(File.Exists(rig.Guard.MarkerPath));

        var (whole, _) = await Stream(synth);

        Assert.Equal(43 * Spt, whole.Samples.Length);
        Assert.False(File.Exists(rig.Guard.MarkerPath));
        Assert.Equal("q4f16/gpu", synth.RenderVariant);
    }

    [Fact]
    public async Task AManagedGpuFailureBeforeAnyAudio_RestartsTheStreamedLineOnTheCpu()
    {
        using var synth = this.StreamingSynth(
            precision: "auto",
            lm16: TinyTurboModels.LmBad16,
            planByLm: lm => lm.Contains("q4f16") ? GpuPlan : DevicePlan.OnCpu("Running on the CPU.", wantedGpu: true));

        var (whole, pieces) = await Stream(synth);

        Assert.Equal(43 * Spt, whole.Samples.Length);
        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Equal(whole.Samples, pieces.SelectMany(p => p).ToArray()); // the failed attempt handed nothing out
        Assert.Equal(EngineState.FellBack, synth.Status.State);
        Assert.Equal("fp32/cpu", synth.RenderVariant);
    }

    [Fact]
    public async Task TheWholeLinePath_IsUntouched_ByTheStreamingSetup()
    {
        using var synth = this.StreamingSynth();

        var audio = await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(43 * Spt, audio.Samples.Length);
        Assert.Null(synth.LastStreamingStats);
    }
}
