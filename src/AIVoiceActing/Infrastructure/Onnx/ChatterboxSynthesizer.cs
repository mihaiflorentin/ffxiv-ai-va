namespace AIVoiceActing.Infrastructure.Onnx;

using System.Collections.Concurrent;
using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// Chatterbox Turbo speech synthesizer (official ResembleAI ONNX export): four sessions, speech encoder
/// (once per reference clip, cached), token embedding, language model (greedy loop with a key-value cache,
/// <see cref="TurboDecodeLoop"/>) and conditional decoder; START/STOP speech tokens 6561/6562, repetition
/// penalty 1.2, a speech-token budget that grows with the text (TurboDecodeLoop.TokenBudget), three SILENCE tokens appended, 24 kHz mono out. Only the language
/// model may run on the GPU; the encoder, embedding and decoder always run on the CPU
/// (docs/plans/2026-10-gpu-spike-results.md). The PerTh watermarker is absent from the ONNX graphs, so
/// output is unwatermarked by construction.
/// </summary>
public sealed class ChatterboxSynthesizer : ISpeechSynthesizer, IDisposable
{
    private const int SampleRate = 24000;
    private const int SamplesPerToken = 960; // 25 speech tokens per second at 24 kHz
    private const int TurboSilenceToken = 4299; // Turbo reference: 3 appended before decode
    private const int SilenceSuffixCount = 3;
    private const int Layers = 24; // config.json text_config.n_layer

    private readonly string modelsDir;
    private readonly Func<string, string?> voicePathResolver;
    private readonly Func<ITextTokenizer>? tokenizerFactory;
    private readonly string executionProvider;
    private readonly ILogSink? log;
    private readonly int? intraOpThreads;
    private readonly TurboDeviceServices? devices;
    private readonly Func<int>? maxPrerollMs;
    private readonly object statusGate = new();
    private volatile Lazy<Engine> engine;
    private DeviceMode? modeOverride;
    private volatile string? loadFailure;
    private volatile string? gpuFailure;
    private volatile bool loading;

    // Unbounded by design: one entry is a few MB (condition embedding + prompt tokens +
    // speaker tensors) and the voice population per session is small (tens — race/gender
    // slots plus overrides), so the cache stays in the low MBs for the process lifetime;
    // no eviction until a real workload shows otherwise. Keyed by precision too: the
    // encoder of each precision produces slightly different embeddings.
    private readonly ConcurrentDictionary<string, ReferenceEmbeddings> referenceCache = new();
    private readonly object tokenizerGate = new();
    private ITextTokenizer? tokenizer;
    private bool tokenizerBroken;
    private string? tokenizerFailure;

    // Single-flight: one synthesis at a time. Concurrent CPU runs pin the machine and
    // multiply KV-cache churn (the 40 GB incident); queued requests hold only their
    // request object until the gate frees.
    private readonly SemaphoreSlim synthesisGate = new(1, 1);
    private volatile bool disposed;

    /// <summary>
    /// Chatterbox Turbo. With <paramref name="devices"/> the sessions follow the device plan (language
    /// model only) and the precision setting; without it everything runs on the CPU at full precision
    /// through the legacy <paramref name="executionProvider"/> string (tools and tests).
    /// </summary>
    public static ChatterboxSynthesizer CreateTurbo(
        string modelsDir,
        Func<string, string?> voicePathResolver,
        string executionProvider = "auto",
        ILogSink? log = null,
        int? intraOpThreads = null,
        Func<ITextTokenizer>? tokenizerFactory = null,
        TurboDeviceServices? devices = null,
        Func<int>? maxPrerollMs = null) =>
        new(
            modelsDir,
            voicePathResolver,
            tokenizerFactory ?? TurboTokenizerFactory(modelsDir),
            executionProvider,
            log,
            intraOpThreads,
            devices,
            maxPrerollMs);

    private static Func<ITextTokenizer> TurboTokenizerFactory(string modelsDir) =>
        () => new TokenizersDotNetTokenizer(Path.Combine(modelsDir, ModelCatalog.TurboTokenizerJsonFileName));

    private ChatterboxSynthesizer(
        string modelsDir,
        Func<string, string?> voicePathResolver,
        Func<ITextTokenizer>? tokenizerFactory,
        string executionProvider,
        ILogSink? log,
        int? intraOpThreads,
        TurboDeviceServices? devices,
        Func<int>? maxPrerollMs)
    {
        this.devices = devices;
        this.maxPrerollMs = maxPrerollMs;
        this.modelsDir = modelsDir;
        this.voicePathResolver = voicePathResolver;
        this.tokenizerFactory = tokenizerFactory;
        this.executionProvider = executionProvider;
        this.intraOpThreads = intraOpThreads;
        this.log = log;
        this.engine = new Lazy<Engine>(this.CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Execution provider the sessions actually run on; populated on first use.</summary>
    public string EffectiveEp => this.engine.IsValueCreated ? this.engine.Value.Ep : "not initialized";

    /// <summary>Precision of the file set in use ("fp32", "fp16", "q4f16"); the planned one before the first load.</summary>
    public string EffectivePrecision => this.engine.IsValueCreated
        ? this.engine.Value.Files.Label
        : this.Resolve(cheap: true).Files.Label;

    /// <summary>Speech tokens generated for the last line (diagnostics: A/B between devices).</summary>
    public IReadOnlyList<long> LastSpeechTokens => this.lastTokens;

    private volatile IReadOnlyList<long> lastTokens = [];

    /// <summary>
    /// What besides the request shapes the audio: the precision of the file set and whether the language
    /// model runs on the GPU. Feeds the render-cache key (the adapter is deliberately not part of it).
    /// </summary>
    public string RenderVariant
    {
        get
        {
            if (this.engine.IsValueCreated && this.engine.Value is { } built)
            {
                return $"{built.Files.Label}/{(built.LmEp == DeviceEp.Cpu ? "cpu" : "gpu")}/{TurboDecodeLoop.Recipe}";
            }

            var resolved = this.Resolve(cheap: true);
            return $"{resolved.Files.Label}/{(resolved.Plan.IsGpu && this.GpuLoopReady ? "gpu" : "cpu")}/{TurboDecodeLoop.Recipe}";
        }
    }

    public bool IsReady => this.FindNotReadyReason() is null;

    private bool GpuLoopReady => this.devices is { LmGpuLoopReady: true };

    /// <summary>
    /// Speech tokens the warm-up line asks the language model for: enough to build its pipelines and to take a first
    /// reading of the device's speed (a median over 15 steps), not enough to cost a second.
    /// </summary>
    private const int WarmUpTokens = 16;

    /// <summary>
    /// Builds all sessions ahead of the first line, then speaks a few words of the default voice and throws
    /// the audio away (clip encoder, a few language model steps, one decoder window), so the shader and
    /// kernel set-up and the first-run costs are paid here and not by the player's first line. Cancellable,
    /// and a failure of the practice line only gets logged: the first real line then meets it as before
    /// (a graphics card that fails moves to the processor there, with its crash-guard handling untouched).
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        // Session creation must not race dispose: take the synthesis gate like a line.
        await this.synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.ThrowIfDisposed();
            var eng = this.engine.Value;
            cancellationToken.ThrowIfCancellationRequested();
            this.WarmLine(eng, cancellationToken);
        }
        finally
        {
            this.synthesisGate.Release();
        }
    }

    private void WarmLine(Engine eng, CancellationToken ct)
    {
        this.loading = true;
        try
        {
            var tokenizer = this.EnsureTokenizer();
            var clip = this.voicePathResolver("default");
            if (tokenizer is null || clip is null || !File.Exists(clip))
            {
                return;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reference = this.ReferenceFor(clip, eng, null);
            var textIds = tokenizer.Encode(BuildPromptText([], "Ready.")).Select(id => (long)id).ToArray();
            var tokens = new List<long>();
            var steps = new LatencyHistogram();
            eng.Loop.Run(
                new TurboDecodeInput(reference.CondEmb, reference.CondShape[1], textIds),
                ct,
                tokens.Add,
                maxNewTokens: WarmUpTokens,
                steps: steps);
            eng.LmGuard?.Succeeded();
            if (eng.CpuLmFactory is not null)
            {
                // A card that is already slow now gets its processor session before the first real line.
                eng.Chooser.Record(LmDevice.Gpu, steps.P50, steps.Count);
                if (eng.Chooser.WantsCpu)
                {
                    eng.BeginCpuBuild(this.log);
                }
            }

            var window = tokens.Concat(Enumerable.Repeat((long)TurboSilenceToken, SilenceSuffixCount)).ToArray();
            _ = this.DecodeWindow(eng, reference.Trimmed(this.Streaming.PromptTokens), window);
            eng.LmGuard?.Succeeded();
            this.log?.Info($"Turbo warm-up line done in {clock.ElapsedMilliseconds} ms ({EngineLabel(eng, eng.LmEp != DeviceEp.Cpu)}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SpeechSynthesisEngineDisposedException and not ObjectDisposedException)
        {
            this.log?.Warn($"Turbo warm-up line failed (the first real line will try again): {ex.Message}");
        }
        finally
        {
            this.loading = false;
        }
    }

    /// <summary>Human-readable reason IsReady is false; empty when ready.</summary>
    public string NotReadyReason => this.FindNotReadyReason() ?? string.Empty;

    /// <summary>
    /// Device state for the UI. With device services it reports where the language model really runs
    /// and why; without them it is plain ready / not installed on the CPU. Cheap enough to poll per
    /// frame: it never hashes files or touches the GPU runtime.
    /// </summary>
    public EngineStatus Status
    {
        get
        {
            if (this.devices is not { } services)
            {
                return EngineStatus.FromReadiness(this.IsReady, this.NotReadyReason);
            }

            DeviceMode requested;
            lock (this.statusGate)
            {
                requested = this.modeOverride ?? services.ModeOverride?.Invoke() ?? services.Planner.Preferences.Mode;
            }

            if (this.loading)
            {
                return new EngineStatus(EngineState.Loading, requested, ActiveDevice.Cpu, "Loading Chatterbox Turbo…", false);
            }

            if (this.loadFailure is { } failure)
            {
                return new EngineStatus(EngineState.Failed, requested, ActiveDevice.Cpu, failure, false);
            }

            if (this.FindNotReadyReason() is { } missing)
            {
                return new EngineStatus(EngineState.NotInstalled, requested, ActiveDevice.Cpu, missing, false);
            }

            var current = this.engine;
            if (current.IsValueCreated && current.Value.DeviceStatus is { } built)
            {
                return AdaptiveStatus(built with { RequestedDevice = requested }, current.Value);
            }

            if (this.gpuFailure is { } gpuDown)
            {
                return new EngineStatus(EngineState.FellBack, requested, ActiveDevice.Cpu, gpuDown, CanRetryGpu: true);
            }

            // Not loaded yet: preview the plan without hashing or loading anything.
            var resolved = this.Resolve(cheap: true);
            return StatusFor(requested, resolved.Plan, services, services.Planner.Preferences.GpuDisabledByCrashGuard, gpuFailed: false);
        }
    }

    /// <summary>What the player is told while lines run on the processor by choice (the card is the engine's plan, the processor is faster right now).</summary>
    public const string OnProcessorForNowText =
        "Running on your processor right now: the game is keeping the graphics card busy, so the processor is faster. It switches back by itself.";

    /// <summary>
    /// An engine that chooses a device per line reports the measured speed of each, and says plainly when its lines are
    /// on the processor for now. Anything else (a CPU plan, a fall back) keeps the status it was built with.
    /// </summary>
    private static EngineStatus AdaptiveStatus(EngineStatus built, Engine eng)
    {
        if (eng.CpuLmFactory is null)
        {
            return built;
        }

        var rates = eng.Chooser.GpuMsPerToken > 0 || eng.Chooser.CpuMsPerToken > 0
            ? new LmDeviceRates(eng.Chooser.GpuMsPerToken, eng.Chooser.CpuMsPerToken)
            : null;
        if (built.State == EngineState.Ready && built.ActiveDevice.IsGpu && eng.Chooser.Preferred == LmDevice.Cpu && eng.CpuLoop is not null)
        {
            return built with { ActiveDevice = ActiveDevice.Cpu, Reason = OnProcessorForNowText, OnProcessorForNow = true, Rates = rates };
        }

        return built with { Rates = rates };
    }

    private DeviceMode? ModeOverride
    {
        get
        {
            lock (this.statusGate)
            {
                return this.modeOverride ?? this.devices?.ModeOverride?.Invoke();
            }
        }
    }

    /// <summary>Maps a plan to a user-facing status. A GPU plan is reported as a CPU run unless the decode loop supports the GPU.</summary>
    private static EngineStatus StatusFor(
        DeviceMode requested,
        DevicePlan plan,
        TurboDeviceServices services,
        bool crashGuardTripped,
        bool gpuFailed)
    {
        if (plan.IsGpu && !services.LmGpuLoopReady)
        {
            return new EngineStatus(
                EngineState.FellBack,
                requested,
                ActiveDevice.Cpu,
                "Chatterbox Turbo runs on the CPU here; GPU speech is not available in this build.",
                false);
        }

        if (plan.IsGpu)
        {
            // Dawn picks the adapter class from the power preference, whatever order the runtime lists them in, so the
            // name comes from the probe's ranking for that preference; with no way to tell, no card is named.
            var adapter = services.Planner.LastReport?.AdapterFor(plan.PowerPreference);
            var name = adapter is null || adapter.NameIsGuess ? string.Empty : adapter.Name;
            return new EngineStatus(EngineState.Ready, requested, new ActiveDevice(plan.Ep, name, plan.Api), plan.Reason, false);
        }

        return plan.WantedGpu
            ? new EngineStatus(EngineState.FellBack, requested, ActiveDevice.Cpu, plan.Reason, crashGuardTripped || gpuFailed)
            : new EngineStatus(EngineState.Ready, requested, ActiveDevice.Cpu, plan.Reason, false);
    }

    /// <summary>
    /// Rebuilds the sessions for <paramref name="mode"/>. A GPU retry first clears the crash-guard
    /// flag and forgets an earlier GPU failure. Waits for the line in flight (single-flight gate), then
    /// loads again, so it is safe to call from a button.
    /// </summary>
    public async Task RetryAsync(DeviceMode mode, CancellationToken cancellationToken = default)
    {
        if (this.devices is not { } services)
        {
            return;
        }

        await this.synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.ThrowIfDisposed();
            if (mode != DeviceMode.Cpu)
            {
                services.ClearCrashFlag?.Invoke();
            }

            var old = this.engine;
            lock (this.statusGate)
            {
                this.modeOverride = mode;
                this.loadFailure = null;
                this.gpuFailure = null;
                this.engine = new Lazy<Engine>(this.CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
            }

            if (old.IsValueCreated)
            {
                old.Value.Dispose();
            }

            _ = await Task.Run(() => this.engine.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SpeechSynthesisException)
        {
            // CreateEngine already recorded the failure for Status.
            this.log?.Warn($"Retry could not load Chatterbox Turbo: {ex.Message}");
        }
        finally
        {
            this.synthesisGate.Release();
        }
    }

    /// <summary>Test hook: the instance has begun its drain-and-teardown sequence.</summary>
    public bool IsDisposed => this.disposed;

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new SpeechSynthesisEngineDisposedException();
        }
    }

    /// <summary>First blocking reason for readiness, or null when the engine can synthesize.</summary>
    private string? FindNotReadyReason()
    {
        // The base files (tokenizer, default voice) are always required, plus ONE complete precision set:
        // the full-size fp32 files (the CPU choice), fp16, or the small q4f16 build a graphics card needs. The
        // default voice is existence-checked below via the resolver: the bundled clip is a PCM16 conversion of
        // the pinned HF download, so its byte size legitimately differs from the catalog pin.
        foreach (var asset in ModelCatalog.TurboBaseAssets)
        {
            if (asset.FileName == ModelCatalog.TurboDefaultVoiceFileName)
            {
                continue;
            }

            if (!this.AssetPresent(asset.FileName, asset.SizeBytes))
            {
                return $"Missing model {asset.FileName} — Models tab → Download all missing.";
            }
        }

        if (!this.AnySetInstalled())
        {
            // Name a file of the set this machine should have, so the hint points at the right download.
            var wanted = this.devices is { } services && (this.ModeOverride ?? services.Planner.Preferences.Mode) != DeviceMode.Cpu
                ? TurboPrecision.Q4F16
                : TurboPrecision.Fp32;
            var set = TurboFiles.For(wanted);
            var missing = ModelCatalog.GroupAssets(set.CatalogGroup)
                .FirstOrDefault(a => !this.AssetPresent(a.FileName, a.SizeBytes));
            return $"Missing model {missing?.FileName ?? set.Lm} — Models tab → Download all missing.";
        }

        // The default reference voice must exist on disk — no clip, no synthesis.
        var voicePath = this.voicePathResolver("default");
        if (voicePath is null || !File.Exists(voicePath))
        {
            return "Missing default reference voice — Models tab → Download all missing.";
        }

        return this.EnsureTokenizer() is null
            ? $"Tokenizer failed to load: {this.tokenizerFailure ?? "unknown error"}."
            : null;
    }

    private bool AnySetInstalled() =>
        new[] { TurboPrecision.Fp32, TurboPrecision.Fp16, TurboPrecision.Q4F16 }
            .Any(precision => TurboFiles.For(precision).IsInstalled(this.modelsDir));

    // Model-presence concern split (review round 3): FileModelStore.IsDownloaded is the
    // single existence+size check the Models tab and provisioner go through; this
    // readiness-specific variant additionally honors the catalog size fallback per
    // session, so it stays local to the engine.
    private bool AssetPresent(string fileName, long? expectedSize)
    {
        var path = Path.Combine(this.modelsDir, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        expectedSize ??= ModelCatalog.Assets
            .FirstOrDefault(a => a.Asset.FileName == fileName)?.Asset.SizeBytes;
        return expectedSize is not { } expected
            || FileModelStore.SizeWithinTolerance(new FileInfo(path).Length, expected);
    }

    public async Task<SynthesisResult> SynthesizeAsync(
        SynthesisRequest request,
        CancellationToken cancellationToken)
    {
        var timer = LineTimer.Current;
        return await Task.Run(
            async () =>
            {
                this.ThrowIfDisposed();
                var waited = System.Diagnostics.Stopwatch.StartNew();
                await this.synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                timer?.AddGateWait(waited.ElapsedMilliseconds);
                try
                {
                    // Disposed while queued behind another line.
                    this.ThrowIfDisposed();
                    var started = Environment.TickCount64;
                    var result = this.SynthesizeCore(request, timer, cancellationToken);
                    this.log?.Info(
                        $"Chatterbox synthesis complete: {result.Samples.Length} samples @ {result.SampleRate} Hz " +
                        $"({(Environment.TickCount64 - started)} ms, voice \"{request.ReferenceVoiceId}\").");
                    return result;
                }
                finally
                {
                    this.synthesisGate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders paralinguistic tags as "[tag] " prompt prefixes before the text, and
    /// guarantees sentence-final punctuation: the LM only emits STOP after it, and an
    /// unpunctuated line otherwise runs to the token cap and decodes as noise
    /// (measured: "this is a test" → 255 tokens of low-frequency rumble;
    /// "This is a test." → 36 tokens of speech).
    /// </summary>
    public static string BuildPromptText(IReadOnlyList<string> tags, string text)
    {
        var prompt = tags.Count == 0
            ? text
            : string.Concat(tags.Select(tag => $"[{tag}] ")) + text;

        return prompt.Length > 0 && !SentenceFinalPunctuation.Contains(prompt[^1])
            ? prompt + "."
            : prompt;
    }

    private static readonly char[] SentenceFinalPunctuation = ['.', '!', '?', '…'];

    public void Dispose()
    {
        lock (this.tokenizerGate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
        }

        // Bounded drain of the in-flight line so the native sessions never die under
        // inference (the engine-switch AV). New requests throw the marker at
        // ThrowIfDisposed instead of entering the gate.
        var drained = this.synthesisGate.Wait(TimeSpan.FromSeconds(10));
        try
        {
            if (this.engine.IsValueCreated)
            {
                this.engine.Value.Dispose();
            }
        }
        finally
        {
            if (drained)
            {
                this.synthesisGate.Release();
            }
        }
    }

    private SynthesisResult SynthesizeCore(SynthesisRequest request, LineTimer? timer, CancellationToken ct)
    {
        this.ThrowIfDisposed();
        var eng = this.engine.Value;
        var tokenizer = this.EnsureTokenizer()
            ?? throw new SpeechSynthesisException("Tokenizer is unavailable (failed to load).");

        var referencePath = this.voicePathResolver(request.ReferenceVoiceId)
            ?? throw new SpeechSynthesisException(
                $"No reference wav resolves for voice id \"{request.ReferenceVoiceId}\".");
        this.log?.Info($"Chatterbox synthesis start: voice \"{request.ReferenceVoiceId}\" → clip {referencePath}.");
        ct.ThrowIfCancellationRequested();

        // Tags arrive as prompt prefixes; Turbo has no exaggeration input.
        var promptText = BuildPromptText(request.Tags, request.Text);
        var textIds = tokenizer.Encode(promptText).Select(id => (long)id).ToArray();

        // A bad reference clip is the clip's problem, not the GPU's: encode (always on the CPU) before the
        // guarded decode.
        var phase = System.Diagnostics.Stopwatch.StartNew();
        var pick = this.PickDevice(eng, request.Text.Length);
        timer?.SetEngine(EngineLabel(eng, pick.OnGpu));
        var reference = this.ReferenceFor(referencePath, eng, timer);
        var referenceMs = phase.ElapsedMilliseconds;
        phase.Restart();
        List<long> generated;
        try
        {
            generated = this.Decode(eng, pick, reference, textIds, timer, ct);
            timer?.AddLanguageModel(SpeechCount(generated), phase.ElapsedMilliseconds);
            this.log?.Info($"Turbo timing: reference {referenceMs} ms, language model {phase.ElapsedMilliseconds} ms for {SpeechCount(generated)} tokens.");
        }
        catch (Exception ex) when (this.IsCpuSessionFailure(eng, pick, ex, ct))
        {
            pick = this.DropCpuSession(eng, ex);
            timer?.ResetAttempt();
            timer?.SetEngine(EngineLabel(eng, pick.OnGpu));
            phase.Restart();
            generated = this.Decode(eng, pick, reference, textIds, timer, ct);
            timer?.AddLanguageModel(SpeechCount(generated), phase.ElapsedMilliseconds);
            this.log?.Info($"Turbo timing: language model {phase.ElapsedMilliseconds} ms for {SpeechCount(generated)} tokens (GPU after a CPU failure).");
        }
        catch (Exception ex) when (this.IsGpuFailure(eng, pick, ex, ct))
        {
            // A managed failure on the GPU means the process is alive: close the crash scope, run this
            // line on the CPU, and stay there until the player retries.
            eng = this.FallBackToCpu(eng, ex);
            pick = this.PickDevice(eng, request.Text.Length);
            timer?.ResetAttempt();
            timer?.SetEngine(EngineLabel(eng, pick.OnGpu));
            reference = this.ReferenceFor(referencePath, eng, timer);
            phase.Restart();
            generated = this.Decode(eng, pick, reference, textIds, timer, ct);
            timer?.AddLanguageModel(SpeechCount(generated), phase.ElapsedMilliseconds);
            this.log?.Info($"Turbo timing: language model {phase.ElapsedMilliseconds} ms for {SpeechCount(generated)} tokens (CPU after a GPU failure).");
        }

        // speech_tokens = generate_tokens[:, 1:-1] — drops the seeded START and the final token (STOP when
        // the loop broke; the card slices identically when the token cap is exhausted).
        var speechOnly = generated.Skip(1).Take(Math.Max(0, generated.Count - 2)).ToArray();
        this.lastTokens = speechOnly;
        var promptTokens = reference.PromptToken;
        var speechTokens = new DenseTensor<long>([1, promptTokens.Length + speechOnly.Length + SilenceSuffixCount]);
        for (var i = 0; i < promptTokens.Length; i++)
        {
            speechTokens[0, i] = promptTokens[i];
        }

        for (var i = 0; i < speechOnly.Length; i++)
        {
            speechTokens[0, promptTokens.Length + i] = speechOnly[i];
        }

        // Turbo reference: silence_tokens = full((n, 3), SILENCE) appended at the end.
        for (var i = 0; i < SilenceSuffixCount; i++)
        {
            speechTokens[0, speechTokens.Dimensions[1] - SilenceSuffixCount + i] = TurboSilenceToken;
        }

        var decoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("speech_tokens", speechTokens),
            NamedOnnxValue.CreateFromTensor("speaker_embeddings", reference.RefXVector),
            NamedOnnxValue.CreateFromTensor("speaker_features", reference.PromptFeat),
        };

        this.log?.Info($"Generated {speechOnly.Length} speech tokens (total {speechTokens.Dimensions[1]} with prompt; {eng.Files.Label} on {(pick.OnGpu ? eng.LmEp : DeviceEp.Cpu)}).");
        float[] wav;
        phase.Restart();
        using (var decoderOutputs = RunSession(eng.Decoder, decoderInputs))
        {
            wav = TensorSpan(unwrap<float>(decoderOutputs[0])).ToArray(); // [1, samples] squeezed below
        }

        timer?.AddDecoder(phase.ElapsedMilliseconds, 1);
        this.log?.Info($"Turbo timing: decoder {phase.ElapsedMilliseconds} ms for {wav.Length / (double)SampleRate:0.0} s of audio.");

        // np.squeeze(axis=0) + clip to [-1, 1].
        var samples = new float[wav.Length];
        for (var i = 0; i < wav.Length; i++)
        {
            samples[i] = Math.Clamp(wav[i], -1f, 1f);
        }

        return new SynthesisResult(samples, SampleRate);
    }

    // ---- chunked (streaming) synthesis ----------------------------------------------------------

    /// <summary>
    /// Forces streaming on or off (tools and tests); null follows the engine: streaming pays off when the
    /// language model runs on the GPU, because only then do tokens arrive faster than they play. On the CPU
    /// the model is slower than real time and the whole-line path wastes less.
    /// </summary>
    public bool? StreamingOverride { get; set; }

    /// <summary>Chunk sizes, context and prompt length of the streaming decode.</summary>
    public StreamingOptions Streaming { get; set; } = StreamingOptions.Default;

    /// <summary>When a streamed line may start to sound (see <see cref="PrerollGate"/>); the player's longest wait overrides <see cref="PrerollOptions.MaxMs"/>.</summary>
    public PrerollOptions Preroll { get; set; } = PrerollOptions.Default;

    /// <summary>What the last streamed line cost (null until one was streamed); for tools and the log.</summary>
    public StreamingStats? LastStreamingStats { get; private set; }

    public bool SupportsStreaming => this.StreamingOverride ?? this.StreamsByPlan();

    /// <summary>The slowest language model, in ms per speech token, that is still streamed: below half of real time no hold would cover the line.</summary>
    private const double SlowestStreamedMsPerToken = 80.0;

    /// <summary>
    /// Every engine that follows a device plan streams, on the processor as on the graphics card: the pre-roll gate
    /// holds the sound back until the rest of the line will be made in time, so a slow machine waits longer instead of
    /// stuttering. The exception is a language model measured slower than half of real time (see
    /// <see cref="SlowestStreamedMsPerToken"/>), where the whole-line render wastes less. Before the engine is built
    /// the plan answers, so a line that arrives early is not rendered whole just because the sessions were not up a
    /// moment ago. Engines without device services (tools, tests) stay on the whole-line path unless forced.
    /// </summary>
    private bool StreamsByPlan()
    {
        if (this.devices is null || this.loadFailure is not null)
        {
            return false;
        }

        var current = this.engine;
        if (current.IsValueCreated)
        {
            var measured = current.Value.MsPerToken;
            return measured <= 0 || measured <= SlowestStreamedMsPerToken;
        }

        return true;
    }

    public int StreamingSampleRate => SampleRate;

    public async Task<SynthesisResult> SynthesizeStreamingAsync(
        SynthesisRequest request,
        Action<ReadOnlyMemory<float>> onSamples,
        CancellationToken cancellationToken)
    {
        var timer = LineTimer.Current;
        return await Task.Run(
            async () =>
            {
                this.ThrowIfDisposed();
                var waited = System.Diagnostics.Stopwatch.StartNew();
                await this.synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                timer?.AddGateWait(waited.ElapsedMilliseconds);
                try
                {
                    this.ThrowIfDisposed();
                    var started = Environment.TickCount64;
                    var result = this.StreamCore(request, onSamples, timer, cancellationToken);
                    this.log?.Info(
                        $"Chatterbox streamed synthesis complete: {result.Samples.Length} samples @ {result.SampleRate} Hz " +
                        $"({Environment.TickCount64 - started} ms, voice \"{request.ReferenceVoiceId}\").");
                    return result;
                }
                finally
                {
                    this.synthesisGate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private SynthesisResult StreamCore(SynthesisRequest request, Action<ReadOnlyMemory<float>> onSamples, LineTimer? timer, CancellationToken ct)
    {
        this.ThrowIfDisposed();
        var eng = this.engine.Value;
        var tokenizer = this.EnsureTokenizer()
            ?? throw new SpeechSynthesisException("Tokenizer is unavailable (failed to load).");
        var referencePath = this.voicePathResolver(request.ReferenceVoiceId)
            ?? throw new SpeechSynthesisException(
                $"No reference wav resolves for voice id \"{request.ReferenceVoiceId}\".");
        this.log?.Info($"Chatterbox streamed synthesis start: voice \"{request.ReferenceVoiceId}\" → clip {referencePath}.");
        ct.ThrowIfCancellationRequested();

        var textIds = tokenizer.Encode(BuildPromptText(request.Tags, request.Text)).Select(id => (long)id).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pick = this.PickDevice(eng, request.Text.Length);
        timer?.SetEngine(EngineLabel(eng, pick.OnGpu));
        var reference = this.ReferenceFor(referencePath, eng, timer);

        var whole = new List<float>();
        long firstAudioMs = -1;
        void Emit(float[] pcm)
        {
            if (pcm.Length == 0)
            {
                return;
            }

            whole.AddRange(pcm);
            if (firstAudioMs < 0)
            {
                firstAudioMs = clock.ElapsedMilliseconds;
            }

            onSamples(pcm);
        }

        StreamingStats stats;
        try
        {
            stats = this.RunStreaming(eng, pick, reference, textIds, request.Text.Length, Emit, timer, ct);
        }
        catch (Exception ex) when (whole.Count == 0 && this.IsCpuSessionFailure(eng, pick, ex, ct))
        {
            pick = this.DropCpuSession(eng, ex);
            timer?.ResetAttempt();
            timer?.SetEngine(EngineLabel(eng, pick.OnGpu));
            stats = this.RunStreaming(eng, pick, reference, textIds, request.Text.Length, Emit, timer, ct);
        }
        catch (Exception ex) when (whole.Count == 0 && this.IsGpuFailure(eng, pick, ex, ct))
        {
            // Nothing was heard yet, so the line can start over on the CPU like a whole-line render.
            eng = this.FallBackToCpu(eng, ex);
            pick = this.PickDevice(eng, request.Text.Length);
            timer?.ResetAttempt();
            timer?.SetEngine(EngineLabel(eng, pick.OnGpu));
            reference = this.ReferenceFor(referencePath, eng, timer);
            stats = this.RunStreaming(eng, pick, reference, textIds, request.Text.Length, Emit, timer, ct);
        }

        this.LastStreamingStats = stats with { FirstAudioMs = firstAudioMs, TotalMs = clock.ElapsedMilliseconds };
        this.log?.Info(
            $"Turbo streaming: first audio {firstAudioMs} ms (held back {stats.PrerollMs} ms), {stats.Chunks} chunks, language model {stats.LanguageModelMs} ms " +
            $"for {stats.SpeechTokens} tokens, decoder {stats.DecoderMs} ms ({eng.Files.Label} on {eng.LmEp}).");
        return new SynthesisResult([.. whole], SampleRate);
    }

    /// <summary>
    /// The language model fills a <see cref="SpeechTokenFeed"/> on a worker while this thread decodes
    /// windows of it as the <see cref="ChunkSchedule"/> releases them and hands the stitched audio to
    /// <paramref name="emit"/>. The worker is always finished (or cancelled) before this returns, so the
    /// sessions are idle when the synthesis gate opens again.
    /// </summary>
    private StreamingStats RunStreaming(
        Engine eng, LinePick pick, ReferenceEmbeddings reference, long[] textIds, int textChars, Action<float[]> emit, LineTimer? timer, CancellationToken ct)
    {
        var options = this.Streaming;
        var gate = new PrerollGate(this.PrerollNow(), textChars, () => Environment.TickCount64, options.Chunks);
        var held = new PrerollBuffer(gate, SampleRate, emit);
        var feed = new SpeechTokenFeed();
        // A language model near real time gets small chunks: the audio then comes in steady pieces and the pre-roll stays short.
        var schedule = new ChunkSchedule(options.Chunks, () => ChunkSchedule.CapFor(options.Chunks, gate.LanguageModelRate));
        var stitcher = new ChunkStitcher(SamplesPerToken, options.HalfFadeSamples);
        var prompt = reference.Trimmed(options.PromptTokens);
        long lmMs = 0;
        var steps = new LatencyHistogram();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(
            () =>
            {
                try
                {
                    var lmClock = System.Diagnostics.Stopwatch.StartNew();
                    pick.Loop.Run(
                        new TurboDecodeInput(reference.CondEmb, reference.CondShape[1], textIds),
                        stop.Token,
                        token =>
                        {
                            feed.Add(token);
                            gate.TokenProduced();
                        },
                        timer,
                        steps: steps);
                    lmMs = lmClock.ElapsedMilliseconds;
                    gate.LanguageModelFinished();
                    if (pick.OnGpu)
                    {
                        eng.LmGuard?.Succeeded();
                    }

                    this.NoteLine(eng, pick, steps);
                    this.lastTokens = feed.SpeechTokens();
                    feed.Finish(Enumerable.Repeat((long)TurboSilenceToken, SilenceSuffixCount));
                }
                catch (Exception ex)
                {
                    feed.Fail(ex);
                }
            },
            CancellationToken.None);

        long decoderMs = 0;
        var chunks = 0;
        try
        {
            while (feed.WaitForPlan(schedule, ct, held.Tick) is { } plan)
            {
                var decoding = System.Diagnostics.Stopwatch.StartNew();
                var window = feed.Slice(plan.WindowStart, plan.WindowEnd);
                var audio = this.DecodeWindow(eng, prompt, window);
                var windowMs = decoding.ElapsedMilliseconds;
                decoderMs += windowMs;
                chunks++;
                var pcm = stitcher.Add(plan, audio);
                gate.WindowDecoded(windowMs, pcm.Length * 1000L / SampleRate);
                held.Add(pcm);
            }

            // Fully made: whatever the pre-roll still holds goes now.
            held.Flush();
        }
        finally
        {
            stop.Cancel();
            producer.Wait();
        }

        var speechTokens = feed.SpeechTokens().Length;
        timer?.AddLanguageModel(speechTokens, lmMs);
        timer?.AddDecoder(decoderMs, chunks);
        var prerollMs = Math.Max(0, gate.WaitedMs);
        timer?.AddPreroll(prerollMs);
        return new StreamingStats(0, 0, chunks, speechTokens, lmMs, decoderMs, stitcher.LengthMismatches, prerollMs);
    }

    /// <summary>Pre-roll settings now: the player's longest wait (Advanced) over the defaults; 0 starts the sound with the first audio.</summary>
    private PrerollOptions PrerollNow() =>
        this.maxPrerollMs?.Invoke() is { } max
            ? this.Preroll with { MaxMs = Math.Clamp(max, 0, 20000) }
            : this.Preroll;

    // ---- which device runs the language model of a line ------------------------------------------------

    /// <summary>The loop a line runs on, and whether that is the graphics card (the crash marker and failure handling follow the card).</summary>
    private sealed record LinePick(TurboDecodeLoop Loop, bool OnGpu);

    /// <summary>
    /// Decides, once and before the line starts, where its language model runs. Only an engine whose language model
    /// is on the graphics card has a choice (Processor = CPU, or a plan that already fell back, never has): it follows
    /// <see cref="LmDeviceChooser"/>, which compares the measured speed of the two. The processor session is built in
    /// the background the first time the card is measured too slow, and the lines stay on the card until it exists.
    /// </summary>
    private LinePick PickDevice(Engine eng, int lineChars)
    {
        if (eng.CpuLmFactory is null)
        {
            return new LinePick(eng.Loop, eng.LmEp != DeviceEp.Cpu);
        }

        if (eng.Chooser.WantsCpu)
        {
            eng.BeginCpuBuild(this.log);
        }

        var cpu = eng.CpuLoop;
        var device = eng.Chooser.Choose(lineChars, cpu is not null);
        var previous = this.lastLmDevice;
        this.lastLmDevice = device;
        if (device != previous && previous is not null)
        {
            this.log?.Info(
                device == LmDevice.Cpu
                    ? $"Turbo language model moves to the processor ({RatesText(eng.Chooser)})."
                    : $"Turbo language model moves back to the graphics card ({RatesText(eng.Chooser)}).");
        }

        return device == LmDevice.Cpu && cpu is not null ? new LinePick(cpu, false) : new LinePick(eng.Loop, true);
    }

    private static string RatesText(LmDeviceChooser chooser) =>
        $"graphics card {chooser.GpuMsPerToken:0} ms and processor {chooser.CpuMsPerToken:0} ms per speech token";

    /// <summary>A line finished: its median step time feeds the choice of device for the next one.</summary>
    private void NoteLine(Engine eng, LinePick pick, LatencyHistogram steps) =>
        eng.Chooser.Record(pick.OnGpu ? LmDevice.Gpu : LmDevice.Cpu, steps.P50, steps.Count);

    private LmDevice? lastLmDevice;

    /// <summary>Where the language model of the last line ran; null before the first line, or when the engine has no choice.</summary>
    public LmDevice? LastLmDevice => this.lastLmDevice;

    /// <summary>The engine's measurements and choice of device (null before the engine is built); for tools and tests.</summary>
    public LmDeviceChooser? DeviceChooser => this.engine.IsValueCreated ? this.engine.Value.Chooser : null;

    /// <summary>True once the processor session of a graphics card engine exists (it is built on demand).</summary>
    public bool CpuSessionReady => this.engine.IsValueCreated && this.engine.Value.CpuLoop is not null;

    private float[] DecodeWindow(Engine eng, ReferenceEmbeddings prompt, long[] window)
    {
        var speechTokens = new DenseTensor<long>([1, prompt.PromptToken.Length + window.Length]);
        for (var i = 0; i < prompt.PromptToken.Length; i++)
        {
            speechTokens[0, i] = prompt.PromptToken[i];
        }

        for (var i = 0; i < window.Length; i++)
        {
            speechTokens[0, prompt.PromptToken.Length + i] = window[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("speech_tokens", speechTokens),
            NamedOnnxValue.CreateFromTensor("speaker_embeddings", prompt.RefXVector),
            NamedOnnxValue.CreateFromTensor("speaker_features", prompt.PromptFeat),
        };

        using var outputs = RunSession(eng.Decoder, inputs);
        var wav = TensorSpan(unwrap<float>(outputs[0])).ToArray();
        for (var i = 0; i < wav.Length; i++)
        {
            wav[i] = Math.Clamp(wav[i], -1f, 1f);
        }

        return wav;
    }

    /// <summary>Speech tokens in a finished loop result: without the seeded START and the closing STOP (the streamed path counts the same).</summary>
    private static int SpeechCount(List<long> generated) =>
        Math.Max(0, generated.Count - (generated.Count > 0 && generated[^1] == TurboDecodeLoop.StopSpeechToken ? 2 : 1));

    private ReferenceEmbeddings ReferenceFor(string referencePath, Engine eng, LineTimer? timer) =>
        this.referenceCache.GetOrAdd(
            $"{eng.Files.Label}|{referencePath}",
            _ =>
            {
                var encoding = System.Diagnostics.Stopwatch.StartNew();
                var encoded = this.EncodeReference(referencePath, eng);
                timer?.AddEncoder(encoding.ElapsedMilliseconds);
                return encoded;
            });

    /// <summary>Engine and place for the timing log, for example "turbo gpu q4f16".</summary>
    private static string EngineLabel(Engine eng, bool onGpu) => $"turbo {(onGpu ? "gpu" : "cpu")} {eng.Files.Label}";

    private List<long> Decode(Engine eng, LinePick pick, ReferenceEmbeddings reference, long[] textIds, LineTimer? timer, CancellationToken ct)
    {
        var steps = new LatencyHistogram();
        var generated = pick.Loop.Run(
            new TurboDecodeInput(reference.CondEmb, reference.CondShape[1], textIds),
            ct,
            timer: timer,
            steps: steps);

        // The language model survived a whole line on this device: the crash marker has done its job.
        if (pick.OnGpu)
        {
            eng.LmGuard?.Succeeded();
        }

        this.NoteLine(eng, pick, steps);
        return generated;
    }

    private bool IsGpuFailure(Engine eng, LinePick pick, Exception ex, CancellationToken ct) =>
        pick.OnGpu
        && eng.LmEp != DeviceEp.Cpu
        && this.devices is not null
        && !ct.IsCancellationRequested
        && ex is not (OperationCanceledException or SpeechSynthesisEngineDisposedException or ObjectDisposedException);

    /// <summary>A line on the background-built processor language model threw (not a cancellation): that session is not to be trusted.</summary>
    private bool IsCpuSessionFailure(Engine eng, LinePick pick, Exception ex, CancellationToken ct) =>
        !pick.OnGpu
        && eng.CpuLmFactory is not null
        && !ct.IsCancellationRequested
        && ex is not (OperationCanceledException or SpeechSynthesisEngineDisposedException or ObjectDisposedException);

    /// <summary>Turns the processor session off for good and returns the graphics card's pick to retry the line on.</summary>
    private LinePick DropCpuSession(Engine eng, Exception ex)
    {
        this.log?.Warn($"The processor language model failed ({ex.GetType().Name}: {ex.Message}); continuing on the graphics card.");
        eng.DisableCpu();
        this.lastLmDevice = LmDevice.Gpu;
        return new LinePick(eng.Loop, true);
    }

    /// <summary>Swaps in a CPU engine after a managed GPU failure. Runs inside the synthesis gate.</summary>
    private Engine FallBackToCpu(Engine failed, Exception ex)
    {
        this.log?.Warn($"The language model failed on the GPU ({ex.GetType().Name}: {ex.Message}); continuing on the CPU.");
        failed.LmGuard?.FailedSafely();
        this.lastLmDevice = null;
        const string reason = "The GPU stopped working while speaking, so Chatterbox Turbo runs on the CPU. Use Retry on GPU to try again.";
        var old = this.engine;
        lock (this.statusGate)
        {
            this.gpuFailure = reason;
            this.engine = new Lazy<Engine>(this.CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        if (old.IsValueCreated)
        {
            old.Value.Dispose();
        }

        return this.engine.Value;
    }

    private ReferenceEmbeddings EncodeReference(string referencePath, Engine eng)
    {
        // Mid-reinstall file race: a vanished/short wav skips one line with a clean
        // error instead of an IO stack trace.
        try
        {
            var audio = WavCodec.ReadMono24k(referencePath); // 24 kHz mono floats (card: librosa sr=24000)
            var audioValues = new DenseTensor<float>([1, audio.Length]);
            audio.AsSpan().CopyTo(audioValues.Buffer.Span);

            using var outputs = RunSession(
                eng.Encoder,
                [NamedOnnxValue.CreateFromTensor("audio_values", audioValues)]);

            // Card destructures positionally: cond_emb, prompt_token, ref_x_vector, prompt_feat.
            // ORT owns output memory — copy every output before the results are disposed.
            var condEmb = unwrap<float>(outputs[0]);
            var promptToken = unwrap<long>(outputs[1]);
            var refXVector = CopyTensor(unwrap<float>(outputs[2]));
            var promptFeat = CopyTensor(unwrap<float>(outputs[3]));

            return new ReferenceEmbeddings(
                TensorSpan(condEmb).ToArray(),
                [.. condEmb.Dimensions],
                TensorSpan(promptToken).ToArray(),
                refXVector,
                promptFeat);
        }
        // FileLoadException derives from IOException but is an ASSEMBLY BIND failure
        // (never "the wav is missing") — rethrow unmasked so the root cause is visible.
        catch (IOException ex) when (ex is not FileLoadException)
        {
            this.log?.Warn($"Reference clip unreadable: {referencePath} ({ex.GetType().Name}: {ex.Message})");
            throw new SpeechSynthesisException($"Reference clip unreadable: {referencePath}", ex);
        }
    }

    private ITextTokenizer? EnsureTokenizer()
    {
        lock (this.tokenizerGate)
        {
            if (this.tokenizer is not null || this.tokenizerBroken)
            {
                return this.tokenizer;
            }

            try
            {
                this.tokenizer = this.tokenizerFactory!();
            }
            catch (Exception ex)
            {
                // Native tokenizer load failures stay non-fatal for IsReady consumers;
                // synthesis surfaces a clean error instead of crashing the plugin.
                this.tokenizerBroken = true;
                this.tokenizerFailure = ex.Message;
                this.log?.Warn($"Tokenizer failed to load: {ex.Message}");
            }

            return this.tokenizer;
        }
    }

    // ---- engine construction -------------------------------------------------------------------

    /// <summary>The file set and device plan for the current settings.</summary>
    private sealed record Resolution(TurboFiles Files, DevicePlan Plan, string Note);

    /// <summary>
    /// Picks the precision set and the plan together: the language model graph decides whether the
    /// allowlist lets it use the GPU, and the precision setting decides which graph that is. "Auto" takes
    /// the first downloaded set whose plan is a GPU one and otherwise the full-precision set on the CPU;
    /// an explicit fp16 or q4f16 uses that set (fp32 when it is not downloaded).
    /// </summary>
    private Resolution Resolve(bool cheap)
    {
        var resolved = this.ResolveCore(cheap);
        // A full-size pin that Processor = Graphics card overrode: say so, unless the GPU failed anyway (that reason wins).
        return this.devices is { } services && this.PinIgnored(services) && resolved.Plan.IsGpu
            ? resolved with { Plan = resolved.Plan with { Reason = PrecisionNotes.FullSizeIgnoredOnGpu } }
            : resolved;
    }

    /// <summary>The precision setting after the processor choice had its say: a full-size pin means nothing on the graphics card.</summary>
    private TurboPrecisionRequest EffectiveRequest(TurboDeviceServices services) =>
        TurboModelSet.Effective(
            TurboModelSet.Parse(services.Precision?.Invoke()),
            this.ModeOverride ?? services.Planner.Preferences.Mode);

    private bool PinIgnored(TurboDeviceServices services) =>
        TurboModelSet.Parse(services.Precision?.Invoke()) == TurboPrecisionRequest.Fp32
        && this.EffectiveRequest(services) != TurboPrecisionRequest.Fp32;

    private Resolution ResolveCore(bool cheap)
    {
        if (this.devices is not { } services)
        {
            return new Resolution(
                TurboFiles.For(TurboPrecision.Fp32), DevicePlan.OnCpu("Running on the CPU."), string.Empty);
        }

        var request = this.EffectiveRequest(services);
        Resolution? first = null;
        var note = string.Empty;
        foreach (var precision in TurboModelSet.Candidates(request))
        {
            var files = TurboFiles.For(precision);
            if (!files.IsInstalled(this.modelsDir))
            {
                if (precision != TurboPrecision.Fp32)
                {
                    note = $"The {files.Label} voice files are not downloaded, so another set is used.";
                }

                continue;
            }

            if (precision == TurboPrecision.Fp32)
            {
                if (first is not null && request == TurboPrecisionRequest.Auto)
                {
                    // Nothing smaller got the GPU: report why through the most preferred set's plan.
                    return new Resolution(files, first.Plan, first.Note);
                }

                var plan = request == TurboPrecisionRequest.Fp32
                    ? DevicePlan.OnCpu("Running the full-precision voice files on the CPU, as you chose.", wantedGpu: false)
                    : services.Planner.PlanFor(DeviceModePolicy.TurboEngineId, this.modelsDir, files.Lm, cheap, this.ModeOverride);
                return new Resolution(files, plan, note);
            }

            var candidate = new Resolution(
                files,
                services.Planner.PlanFor(DeviceModePolicy.TurboEngineId, this.modelsDir, files.Lm, cheap, this.ModeOverride),
                note);
            if (request != TurboPrecisionRequest.Auto || candidate.Plan.IsGpu)
            {
                return candidate;
            }

            first ??= candidate;
        }

        // No set the plan wanted is installed. A smaller set that only got a CPU plan still speaks (a GPU install
        // whose graphics card is switched off); with nothing installed readiness reports the missing files.
        if (first is not null)
        {
            return first;
        }

        var fallback = TurboFiles.For(TurboPrecision.Fp32);
        return new Resolution(fallback, DevicePlan.OnCpu("Running on the CPU."), note);
    }

    private Engine CreateEngine()
    {
        this.loading = true;
        try
        {
            return this.devices is { } services ? this.CreateTurboEngine(services) : this.CreateCpuEngine();
        }
        catch (Exception ex)
        {
            this.loadFailure = $"Chatterbox Turbo could not load: {ex.Message}";
            throw;
        }
        finally
        {
            this.loading = false;
        }
    }

    /// <summary>
    /// Turbo sessions from the device plan: only the language model may leave the CPU, and only
    /// when the plan allows it and the decode loop supports it. A managed GPU failure rebuilds the
    /// whole set on the CPU; a native abort is the crash guard's business.
    /// </summary>
    private Engine CreateTurboEngine(TurboDeviceServices services)
    {
        var requested = this.ModeOverride ?? services.Planner.Preferences.Mode;
        var crashFlag = services.Planner.Preferences.GpuDisabledByCrashGuard;
        var resolved = this.Resolve(cheap: false);
        var plan = resolved.Plan;
        this.log?.Info($"Chatterbox Turbo device plan: {plan.Ep}, {resolved.Files.Label} ({plan.Reason})");

        var gpuFailed = false;
        if (plan.IsGpu && services.LmGpuLoopReady && this.gpuFailure is null)
        {
            try
            {
                var gpuEngine = this.BuildSessions(services, plan, resolved.Files);
                gpuEngine.DeviceStatus = gpuEngine.LmEp == DeviceEp.Cpu
                    ? StatusFor(requested, DevicePlan.OnCpu(gpuEngine.FallbackReason, wantedGpu: true), services, crashFlag, gpuFailed: true)
                    : StatusFor(requested, plan, services, crashFlag, gpuFailed: false);
                return gpuEngine;
            }
            catch (Exception ex)
            {
                this.log?.Warn($"Turbo sessions failed on the GPU ({ex.Message}); retrying everything on the CPU.");
                gpuFailed = true;
            }
        }

        // CPU build: the plan said so, the decode loop is not GPU-ready, the GPU failed before, or its build failed.
        var cpuFiles = plan.IsGpu ? this.CpuFilesFor(resolved.Files) : resolved.Files;
        var cpuPlan = plan.IsGpu
            ? DevicePlan.OnCpu(plan.Reason, wantedGpu: true, plan.PowerPreference)
            : plan;
        var cpuEngine = this.BuildSessions(services, cpuPlan, cpuFiles);
        var priorFailure = plan.IsGpu ? this.gpuFailure : null;
        cpuEngine.DeviceStatus = priorFailure is not null
            ? new EngineStatus(EngineState.FellBack, requested, ActiveDevice.Cpu, priorFailure, CanRetryGpu: true)
            : gpuFailed
                ? new EngineStatus(
                    EngineState.FellBack,
                    requested,
                    ActiveDevice.Cpu,
                    "The GPU could not be started, so Chatterbox Turbo runs on the CPU.",
                    CanRetryGpu: true)
                : StatusFor(requested, plan, services, crashFlag, gpuFailed: false);
        return cpuEngine;
    }

    /// <summary>The set to use on the CPU when the GPU plan did not work out: auto goes back to full precision.</summary>
    private TurboFiles CpuFilesFor(TurboFiles chosen)
    {
        var request = this.devices is { } services ? this.EffectiveRequest(services) : TurboPrecisionRequest.Auto;
        var full = TurboFiles.For(TurboPrecision.Fp32);
        return request == TurboPrecisionRequest.Auto && full.IsInstalled(this.modelsDir) ? full : chosen;
    }

    private Engine BuildSessions(TurboDeviceServices services, DevicePlan plan, TurboFiles files)
    {
        var builds = new List<SessionBuild>();
        var sessions = new List<InferenceSession>();
        try
        {
            SessionBuild Build(SessionRole role)
            {
                var build = services.Sessions.Create(role, plan, this.intraOpThreads);
                builds.Add(build);
                return build;
            }

            var encoder = this.CreateSession(files.Encoder, Build(SessionRole.Encoder).Options, services);
            sessions.Add(encoder);
            var embed = this.CreateSession(files.Embed, Build(SessionRole.Embedding).Options, services);
            sessions.Add(embed);
            var lmBuild = Build(SessionRole.Lm);
            var lm = this.CreateSession(files.Lm, lmBuild.Options, services);
            sessions.Add(lm);
            var decoder = this.CreateSession(files.Decoder, Build(SessionRole.Decoder).Options, services);
            sessions.Add(decoder);
            this.log?.Info($"Chatterbox Turbo sessions initialized ({files.Label}, language model on {lmBuild.Ep}).");
            return new Engine(
                encoder, embed, lm, decoder, lmBuild.Ep.ToString().ToLowerInvariant(), builds.Select(b => b.Options).ToArray(), files)
            {
                LmEp = lmBuild.Ep,
                FallbackReason = lmBuild.FallbackReason,
                LmGuard = lmBuild.Guard,
                KvMemory = lmBuild.IsGpu ? lmBuild.DeviceMemory : null,

                // A graphics card engine can also run a line on the processor; that session is only built when the card proves slow.
                CpuLmFactory = lmBuild.IsGpu ? () => this.BuildCpuLanguageModel(services, files) : null,
            };
        }
        catch (Exception)
        {
            foreach (var session in sessions)
            {
                session.Dispose();
            }

            foreach (var build in builds)
            {
                build.Options.Dispose();
                build.Guard?.FailedSafely();
            }

            throw;
        }
    }

    /// <summary>
    /// A second language model session of the engine's own file set on the processor, with the player's thread count
    /// (Background load). About 0.2 to 0.6 GB of memory, so it is built on demand and kept until the engine goes.
    /// </summary>
    private (InferenceSession Session, SessionOptions Options) BuildCpuLanguageModel(TurboDeviceServices services, TurboFiles files)
    {
        var build = services.Sessions.Create(SessionRole.Lm, DevicePlan.OnCpu("Language model on the processor, beside the graphics card."), this.intraOpThreads);
        try
        {
            return (this.CreateSession(files.Lm, build.Options, services), build.Options);
        }
        catch
        {
            build.Options.Dispose();
            throw;
        }
    }

    /// <summary>No device services (tools, tests): everything on the CPU at full precision, provider string as given.</summary>
    private Engine CreateCpuEngine()
    {
        var files = TurboFiles.For(TurboPrecision.Fp32);
        SessionOptions? options = null;
        var created = new List<InferenceSession>(); // disposed if a later session fails
        try
        {
            var (epOptions, ep) = EpSelector.CreateSessionOptions(
                this.executionProvider,
                message => this.log?.Info(message),
                this.intraOpThreads);
            options = epOptions;
            created.Add(this.CreateSession(files.Encoder, epOptions, null));
            created.Add(this.CreateSession(files.Embed, epOptions, null));
            created.Add(this.CreateSession(files.Lm, epOptions, null));
            created.Add(this.CreateSession(files.Decoder, epOptions, null));
            this.log?.Info($"Chatterbox Turbo sessions initialized on EP '{ep}'.");
            return new Engine(created[0], created[1], created[2], created[3], ep, [options], files);
        }
        catch (Exception) when (this.executionProvider != "cpu")
        {
            // Provider-level failure at session creation (e.g. CoreML rejects a graph):
            // retry everything on CPU so the engine still works.
            this.log?.Warn("Execution provider rejected a session; retrying on CPU.");
            foreach (var session in created)
            {
                session.Dispose();
            }

            options?.Dispose();
            var cpuOptions = EpSelector.CreateSessionOptions("cpu", intraOpThreads: this.intraOpThreads).Options;
            return new Engine(
                this.CreateSession(files.Encoder, cpuOptions, null),
                this.CreateSession(files.Embed, cpuOptions, null),
                this.CreateSession(files.Lm, cpuOptions, null),
                this.CreateSession(files.Decoder, cpuOptions, null),
                "cpu",
                [cpuOptions],
                files);
        }
    }

    private InferenceSession CreateSession(string fileName, SessionOptions options, TurboDeviceServices? services)
    {
        var path = Path.Combine(this.modelsDir, fileName);
        if (!File.Exists(path))
        {
            throw new SpeechSynthesisException($"Model file missing: {fileName} (download it from the Models tab).");
        }

        return services?.OpenSession is { } open ? open(path, options) : new InferenceSession(path, options);
    }

    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunSession(
        InferenceSession session,
        IReadOnlyList<NamedOnnxValue> inputs)
    {
        var outputs = session.Run(inputs);
        if (outputs.Count == 0)
        {
            outputs.Dispose();
            throw new SpeechSynthesisException("Session returned no outputs.");
        }

        return outputs;
    }

    private static Tensor<T> unwrap<T>(DisposableNamedOnnxValue value)
        where T : unmanaged =>
        value.AsTensor<T>();

    private static ReadOnlySpan<T> TensorSpan<T>(Tensor<T> tensor)
        where T : unmanaged =>
        ((DenseTensor<T>)tensor).Buffer.Span;

    private static DenseTensor<T> CopyTensor<T>(Tensor<T> tensor)
        where T : unmanaged
    {
        var copy = new DenseTensor<T>([.. tensor.Dimensions]);
        TensorSpan(tensor).CopyTo(copy.Buffer.Span);
        return copy;
    }

    private sealed record ReferenceEmbeddings(
        float[] CondEmb,
        int[] CondShape,
        long[] PromptToken,
        DenseTensor<float> RefXVector,
        DenseTensor<float> PromptFeat)
    {
        /// <summary>
        /// The last <paramref name="tokens"/> prompt tokens with their mel frames (two per token), the part of
        /// the reference closest to where the new speech continues. The decoder pays for every prompt token on
        /// every call, so chunked decoding keeps only a short prompt; the speaker embedding is unchanged.
        /// </summary>
        public ReferenceEmbeddings Trimmed(int tokens)
        {
            var (kept, feat) = PromptTrim.Tail(this.PromptToken, this.PromptFeat, tokens);
            return ReferenceEquals(kept, this.PromptToken) ? this : this with { PromptToken = kept, PromptFeat = feat };
        }
    }

    private sealed class Engine(
        InferenceSession encoder,
        InferenceSession embedTokens,
        InferenceSession languageModel,
        InferenceSession decoder,
        string ep,
        IReadOnlyList<SessionOptions> options,
        TurboFiles files) : IDisposable
    {
        private TurboDecodeLoop? loop;

        public InferenceSession Encoder { get; } = encoder;
        public InferenceSession EmbedTokens { get; } = embedTokens;
        public InferenceSession LanguageModel { get; } = languageModel;
        public InferenceSession Decoder { get; } = decoder;
        public string Ep { get; } = ep;
        public TurboFiles Files { get; } = files;
        private IReadOnlyList<SessionOptions> Options { get; } = options;

        /// <summary>Turbo with device services only: what the UI shows for this build.</summary>
        public EngineStatus? DeviceStatus { get; set; }

        /// <summary>Provider the language model session got.</summary>
        public DeviceEp LmEp { get; init; } = DeviceEp.Cpu;

        /// <summary>Why a wanted GPU session became a CPU one; empty otherwise.</summary>
        public string FallbackReason { get; init; } = string.Empty;

        /// <summary>GPU buffer memory for the key-value cache; null keeps it in CPU memory.</summary>
        public OrtMemoryInfo? KvMemory { get; init; }

        /// <summary>
        /// Crash-guard scope of a GPU language model session. The synthesizer completes it after the
        /// first line decoded (and fails it on a managed exception); until then the marker stays on
        /// disk. Null on the CPU.
        /// </summary>
        public GpuCrashGuard.Scope? LmGuard { get; init; }

        /// <summary>The decode loop over this engine's embedding and language model sessions (built on first use).</summary>
        public TurboDecodeLoop Loop => this.loop ??= new TurboDecodeLoop(
            this.EmbedTokens, this.LanguageModel, this.KvMemory ?? OrtMemoryInfo.DefaultInstance, Layers);

        /// <summary>Measures both devices and picks one per line (it only ever picks the graphics card when <see cref="CpuLmFactory"/> is null).</summary>
        public LmDeviceChooser Chooser { get; } = new();

        /// <summary>Builds the processor language model beside a graphics card one; null when the engine has no such choice.</summary>
        public Func<(InferenceSession Session, SessionOptions Options)>? CpuLmFactory { get; init; }

        private readonly object cpuGate = new();
        private bool cpuBuildStarted;
        private bool disposed;
        private InferenceSession? cpuLm;
        private SessionOptions? cpuOptions;
        private volatile TurboDecodeLoop? cpuLoop;

        /// <summary>The measured ms per speech token of the device the lines run on; 0 before any line.</summary>
        public double MsPerToken => this.CpuLmFactory is not null
            ? this.Chooser.PreferredMsPerToken
            : this.LmEp == DeviceEp.Cpu ? this.Chooser.CpuMsPerToken : this.Chooser.GpuMsPerToken;

        /// <summary>The decode loop of the processor session once it exists; null until then.</summary>
        public TurboDecodeLoop? CpuLoop => this.cpuLoop;

        /// <summary>Gives the processor session up after it failed a line: it is disposed and never built or chosen again.</summary>
        public void DisableCpu()
        {
            this.Chooser.DisableCpu();
            lock (this.cpuGate)
            {
                this.cpuBuildStarted = true;
                this.cpuLoop = null;
                this.cpuLm?.Dispose();
                this.cpuOptions?.Dispose();
                this.cpuLm = null;
                this.cpuOptions = null;
            }
        }

        /// <summary>Starts building the processor session on a worker; does nothing when it was started, failed, or the engine is gone.</summary>
        public void BeginCpuBuild(ILogSink? log)
        {
            if (this.CpuLmFactory is not { } factory)
            {
                return;
            }

            lock (this.cpuGate)
            {
                if (this.cpuBuildStarted || this.disposed)
                {
                    return;
                }

                this.cpuBuildStarted = true;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var (session, options) = factory();
                    lock (this.cpuGate)
                    {
                        if (this.disposed)
                        {
                            session.Dispose();
                            options.Dispose();
                            return;
                        }

                        this.cpuLm = session;
                        this.cpuOptions = options;
                        this.cpuLoop = new TurboDecodeLoop(this.EmbedTokens, session, OrtMemoryInfo.DefaultInstance, Layers);
                    }

                    log?.Info($"Turbo processor language model ready in {clock.ElapsedMilliseconds} ms (the graphics card was slow; lines may move to it).");
                }
                catch (Exception ex)
                {
                    // Not retried: the lines simply stay on the graphics card.
                    log?.Warn($"Turbo processor language model could not be built ({ex.GetType().Name}: {ex.Message}).");
                }
            });
        }

        public void Dispose()
        {
            lock (this.cpuGate)
            {
                this.disposed = true;
                this.cpuLoop = null;
                this.cpuLm?.Dispose();
                this.cpuOptions?.Dispose();
                this.cpuLm = null;
                this.cpuOptions = null;
            }

            this.Encoder.Dispose();
            this.EmbedTokens.Dispose();
            this.LanguageModel.Dispose();
            this.Decoder.Dispose();
            foreach (var options in this.Options)
            {
                options.Dispose();
            }

            // A scope still open here never saw its first line; the process is alive, so
            // nothing crashed and the marker must not condemn the next start.
            this.LmGuard?.FailedSafely();
        }
    }
}
