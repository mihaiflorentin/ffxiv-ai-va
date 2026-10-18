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
        Assert.Equal("fp32/cpu/w32r12", synth.RenderVariant);
    }

    [Fact]
    public async Task OnTheCpu_Float16Cache_ForTheSmallerPrecisions()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        var rig = this.NewRig(dir, "q4f16");
        using var synth = rig.Synth;

        await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Equal("q4f16/cpu/w32r12", synth.RenderVariant);
    }

    [Fact]
    public async Task OnAGpuPlan_TheSameLoopRuns_AndTheFirstFinishedLineClosesTheCrashMarker()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        var rig = this.NewRig(dir, "auto", plan: GpuPlan);
        using var synth = rig.Synth;
        await synth.WarmUpAsync(CancellationToken.None);
        Assert.False(File.Exists(rig.Guard.MarkerPath), "the warm-up ran the language model and the decoder on the GPU: the marker is closed");

        await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.False(File.Exists(rig.Guard.MarkerPath));
        Assert.Equal("q4f16/gpu/w32r12", synth.RenderVariant);
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
        Assert.Equal("fp32/cpu/w32r12", synth.RenderVariant);

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

        // Build the sessions without the warm-up line, which would remember the default clip's embeddings.
        await synth.RetryAsync(DeviceMode.Auto);
        File.Delete(rig.Voice);

        await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Line(), CancellationToken.None));

        Assert.Equal("q4f16/gpu/w32r12", synth.RenderVariant); // still the GPU engine: no fallback happened
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
    public async Task TheWarmUp_SpeaksAPracticeLine_SoTheDefaultClipIsAlreadyEncodedAndNothingIsHeard()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16, decoder: TinyTurboModels.StreamDecoder);
        var rig = this.NewRig(dir, "fp32");
        using var synth = rig.Synth;
        var before = synth.LastSpeechTokens;

        await synth.WarmUpAsync(CancellationToken.None);
        Assert.Equal(EngineState.Ready, synth.Status.State); // the "Loading" state of the practice line is over
        File.Delete(rig.Voice); // a real line now finds the clip's embeddings in memory

        var audio = await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(43 * Spt, audio.Samples.Length);
        Assert.Empty(before);
    }

    [Fact]
    public async Task TheWarmUp_StopsWhenCancelled()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        using var synth = this.NewRig(dir, "fp32").Synth;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synth.WarmUpAsync(cts.Token));
    }

    [Fact]
    public async Task APrerollOfZero_HandsEachPieceOverAsItDecodes_ADefaultOneKeepsTheStartBackAndSaysSo()
    {
        using var synth = this.StreamingSynth();
        synth.Preroll = new PrerollOptions(MaxMs: 0);
        var (_, immediate) = await Stream(synth);
        Assert.True(immediate.Count >= 5);
        Assert.Equal(0, synth.LastStreamingStats!.PrerollMs);

        synth.Preroll = PrerollOptions.Default;
        var (whole, held) = await Stream(synth);
        Assert.Equal(whole.Samples, held.SelectMany(p => p).ToArray());
        Assert.True(synth.LastStreamingStats!.PrerollMs >= 0);
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
    public async Task StreamingIsOffered_OnTheGpuAndOnTheCpu_AndOffForANoDeviceEngine_UnlessForced()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16, decoder: TinyTurboModels.StreamDecoder);
        using var gpu = this.NewRig(dir, "auto", plan: GpuPlan).Synth;
        Assert.True(gpu.SupportsStreaming); // not loaded yet: the plan answers, so an early line is not rendered whole
        await gpu.WarmUpAsync(CancellationToken.None);
        Assert.True(gpu.SupportsStreaming);

        using var cpu = this.NewRig(this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16), "fp32").Synth;
        Assert.True(cpu.SupportsStreaming); // the pre-roll gate protects a slow processor, so it streams too
        await cpu.WarmUpAsync(CancellationToken.None);
        Assert.True(cpu.SupportsStreaming);
        cpu.StreamingOverride = false;
        Assert.False(cpu.SupportsStreaming);
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
        Assert.False(File.Exists(rig.Guard.MarkerPath), "the warm-up ran the language model and the decoder on the GPU: the marker is closed");

        var (whole, _) = await Stream(synth);

        Assert.Equal(43 * Spt, whole.Samples.Length);
        Assert.False(File.Exists(rig.Guard.MarkerPath));
        Assert.Equal("q4f16/gpu/w32r12", synth.RenderVariant);
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
        Assert.Equal("fp32/cpu/w32r12", synth.RenderVariant);
    }

    [Fact]
    public async Task TheWholeLinePath_IsUntouched_ByTheStreamingSetup()
    {
        using var synth = this.StreamingSynth();

        var audio = await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(43 * Spt, audio.Samples.Length);
        Assert.Null(synth.LastStreamingStats);
    }

    [Theory]
    [InlineData(1, 256)]
    [InlineData(35, 620)]
    [InlineData(500, 1000)]
    public void TokenBudget_GrowsWithTheText_BetweenTheFloorAndTheCeiling(int textTokens, int expected)
    {
        // 35 text tokens is a 150-character line: a fixed 256 cut such lines mid-sentence.
        Assert.Equal(expected, TurboDecodeLoop.TokenBudget(textTokens));
    }

    [Fact]
    public void RepetitionPenalty_OnlyLooksAtTheRecentWindow()
    {
        // A word said 40 tokens ago may be said again: only the last 32 picks are penalised.
        var generated = new List<long> { TurboDecodeLoop.StartSpeechToken, 7 };
        generated.AddRange(Enumerable.Range(100, 40).Select(i => (long)i));

        var recent = TurboDecodeLoop.RecentDistinct(generated, TurboDecodeLoop.PenaltyWindow);

        Assert.DoesNotContain(7L, recent);
        Assert.Contains(139L, recent);
        Assert.Equal(TurboDecodeLoop.PenaltyWindow, recent.Count);
    }

    [Fact]
    public void ARunOfIdenticalTokens_IsBrokenAtTheCap()
    {
        var logits = new float[10];
        logits[4] = 5f;
        var generated = Enumerable.Repeat(4L, TurboDecodeLoop.MaxRun).ToList();

        TurboDecodeLoop.CapRun(logits, generated, TurboDecodeLoop.MaxRun);

        Assert.Equal(float.NegativeInfinity, logits[4]);
        Assert.NotEqual(4L, TurboDecodeLoop.Argmax(logits));
    }

    [Fact]
    public void AShorterRun_IsLeftAlone()
    {
        var logits = new float[10];
        logits[4] = 5f;
        var generated = new List<long> { 3 };
        generated.AddRange(Enumerable.Repeat(4L, TurboDecodeLoop.MaxRun - 1));

        TurboDecodeLoop.CapRun(logits, generated, TurboDecodeLoop.MaxRun);

        Assert.Equal(4L, TurboDecodeLoop.Argmax(logits));
    }

    // ---- streaming on the processor, and the choice of device per line ----------------------------------

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task OnTheCpu_ALineStreamsByItself_InSeveralPieces()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16, decoder: TinyTurboModels.StreamDecoder);
        using var synth = this.NewRig(dir, "fp32").Synth;
        synth.Streaming = TinyChunks;
        Assert.True(synth.SupportsStreaming); // no override: the processor streams too

        var (whole, pieces) = await Stream(synth);

        Assert.True(pieces.Count >= 5, $"expected the line in several pieces, got {pieces.Count}");
        Assert.Equal(whole.Samples, pieces.SelectMany(p => p).ToArray());
        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Null(synth.LastLmDevice); // a processor engine has no choice to make
        Assert.False(synth.CpuSessionReady);
    }

    [Fact]
    public async Task AProcessorMeasuredSlowerThanHalfRealTime_IsNotStreamed()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        using var synth = this.NewRig(dir, "fp32").Synth;
        await synth.WarmUpAsync(CancellationToken.None);

        synth.DeviceChooser!.Record(LmDevice.Cpu, 120, 50);

        Assert.False(synth.SupportsStreaming);
        synth.DeviceChooser.Record(LmDevice.Cpu, 20, 50);
        synth.DeviceChooser.Record(LmDevice.Cpu, 20, 50);
        synth.DeviceChooser.Record(LmDevice.Cpu, 20, 50);
        synth.DeviceChooser.Record(LmDevice.Cpu, 20, 50);
        synth.DeviceChooser.Record(LmDevice.Cpu, 20, 50);
        Assert.True(synth.SupportsStreaming);
    }

    [Fact]
    public async Task AGraphicsCardThatKeepsUp_NeverBuildsTheProcessorSession()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        using var synth = this.NewRig(dir, "auto", plan: GpuPlan).Synth;
        await synth.WarmUpAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            synth.DeviceChooser!.Record(LmDevice.Gpu, 8, 50);
            await synth.SynthesizeAsync(Line(), CancellationToken.None);
        }

        Assert.Equal(LmDevice.Gpu, synth.LastLmDevice);
        Assert.False(synth.CpuSessionReady);
        Assert.False(synth.Status.OnProcessorForNow);
    }

    [Fact]
    public async Task AGraphicsCardMeasuredSlow_GetsAProcessorSession_AndTheNextLineMovesThere_WithTheSameSpeech()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16);
        var rig = this.NewRig(dir, "auto", plan: GpuPlan);
        using var synth = rig.Synth;
        await synth.WarmUpAsync(CancellationToken.None);
        var chooser = synth.DeviceChooser!;
        Assert.False(synth.CpuSessionReady);

        // The card is busy with the game: the first line still runs on it while the processor session is built.
        for (var i = 0; i < 6; i++)
        {
            chooser.Record(LmDevice.Gpu, 45, 50);
        }

        await synth.SynthesizeAsync(Line(), CancellationToken.None);
        Assert.Equal(LmDevice.Gpu, synth.LastLmDevice);
        Assert.True(await Eventually(() => synth.CpuSessionReady), "the processor session never came up");

        for (var i = 0; i < 6; i++)
        {
            chooser.Record(LmDevice.Gpu, 45, 50);
        }

        var audio = await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.Equal(LmDevice.Cpu, synth.LastLmDevice);
        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
        Assert.Equal(48, audio.Samples.Length);
        var status = synth.Status;
        Assert.Equal(EngineState.Ready, status.State);
        Assert.True(status.OnProcessorForNow);
        Assert.False(status.ActiveDevice.IsGpu);
        Assert.Equal(ChatterboxSynthesizer.OnProcessorForNowText, status.Reason);
        Assert.True(status.Rates!.GpuMsPerToken > 30);
        Assert.Equal("q4f16/gpu/w32r12", synth.RenderVariant); // the engine's plan; the cache key does not flip with the load
        Assert.False(File.Exists(rig.Guard.MarkerPath));
    }

    [Fact]
    public async Task AStreamedLine_AlsoFollowsTheChoice_AndIsTheSameLineOnTheProcessor()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.Lm16, decoder: TinyTurboModels.StreamDecoder);
        using var synth = this.NewRig(dir, "auto", plan: GpuPlan).Synth;
        synth.Streaming = TinyChunks;
        await synth.WarmUpAsync(CancellationToken.None);
        var chooser = synth.DeviceChooser!;
        for (var i = 0; i < 6; i++)
        {
            chooser.Record(LmDevice.Gpu, 45, 50);
        }

        var (onCard, _) = await Stream(synth); // the processor session is built behind this one
        Assert.True(await Eventually(() => synth.CpuSessionReady));
        for (var i = 0; i < 6; i++)
        {
            chooser.Record(LmDevice.Gpu, 45, 50);
        }

        var (onProcessor, pieces) = await Stream(synth);

        Assert.Equal(LmDevice.Cpu, synth.LastLmDevice);
        Assert.True(pieces.Count >= 5);
        Assert.Equal(onCard.Samples, onProcessor.Samples);
        Assert.Equal(ExpectedTokens, synth.LastSpeechTokens);
    }

    [Fact]
    public async Task APlanThatFellBackToTheProcessor_NeverGetsASecondSession()
    {
        var dir = this.Install(TinyTurboModels.Lm32, TinyTurboModels.LmBad16);
        var rig = this.NewRig(dir, "auto", planByLm: lm => lm.Contains("q4f16") ? GpuPlan : DevicePlan.OnCpu("Running on the CPU.", wantedGpu: true));
        using var synth = rig.Synth;
        await synth.SynthesizeAsync(Line(), CancellationToken.None); // the card fails, the line finishes on the processor

        synth.DeviceChooser!.Record(LmDevice.Gpu, 90, 50);
        await synth.SynthesizeAsync(Line(), CancellationToken.None);

        Assert.False(synth.CpuSessionReady);
        Assert.Equal(EngineState.FellBack, synth.Status.State);
        Assert.False(synth.Status.OnProcessorForNow);
    }
}
