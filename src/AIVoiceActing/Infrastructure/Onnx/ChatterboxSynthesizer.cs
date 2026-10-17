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
/// penalty 1.2, max 256 new tokens, three SILENCE tokens appended, 24 kHz mono out. Only the language
/// model may run on the GPU; the encoder, embedding and decoder always run on the CPU
/// (docs/plans/2026-10-gpu-spike-results.md). The PerTh watermarker is absent from the ONNX graphs, so
/// output is unwatermarked by construction.
/// </summary>
public sealed class ChatterboxSynthesizer : ISpeechSynthesizer, IDisposable
{
    private const int SampleRate = 24000;
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
        TurboDeviceServices? devices = null) =>
        new(
            modelsDir,
            voicePathResolver,
            tokenizerFactory ?? TurboTokenizerFactory(modelsDir),
            executionProvider,
            log,
            intraOpThreads,
            devices);

    private static Func<ITextTokenizer> TurboTokenizerFactory(string modelsDir) =>
        () => new TokenizersDotNetTokenizer(Path.Combine(modelsDir, ModelCatalog.TurboTokenizerJsonFileName));

    private ChatterboxSynthesizer(
        string modelsDir,
        Func<string, string?> voicePathResolver,
        Func<ITextTokenizer>? tokenizerFactory,
        string executionProvider,
        ILogSink? log,
        int? intraOpThreads,
        TurboDeviceServices? devices)
    {
        this.devices = devices;
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
                return $"{built.Files.Label}/{(built.LmEp == DeviceEp.Cpu ? "cpu" : "gpu")}";
            }

            var resolved = this.Resolve(cheap: true);
            return $"{resolved.Files.Label}/{(resolved.Plan.IsGpu && this.GpuLoopReady ? "gpu" : "cpu")}";
        }
    }

    public bool IsReady => this.FindNotReadyReason() is null;

    private bool GpuLoopReady => this.devices is { LmGpuLoopReady: true };

    /// <summary>Builds all sessions ahead of the first line; no-ops once initialized.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        // Session creation must not race dispose: take the synthesis gate like a line.
        await this.synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.ThrowIfDisposed();
            _ = this.engine.Value;
        }
        finally
        {
            this.synthesisGate.Release();
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
                return built with { RequestedDevice = requested };
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
            // Dawn picks the adapter class from the power preference; the name is display only.
            var adapters = services.Planner.LastReport?.Adapters ?? [];
            var adapter = adapters.Count == 0
                ? string.Empty
                : (plan.PowerPreference == GpuPreference.LowPower ? adapters[^1] : adapters[0]).Name;
            return new EngineStatus(EngineState.Ready, requested, new ActiveDevice(plan.Ep, adapter, plan.Api), plan.Reason, false);
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
        // The base fp32 set (graphs, weights, tokenizer, default voice) is always required: it is the CPU
        // choice and the fallback when a smaller precision set is missing. The default voice is
        // existence-checked below via the resolver: the bundled clip is a PCM16 conversion of the pinned
        // HF download, so its byte size legitimately differs from the catalog pin.
        foreach (var asset in ModelCatalog.TurboRequiredAssets)
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
        return await Task.Run(
            async () =>
            {
                this.ThrowIfDisposed();
                await this.synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Disposed while queued behind another line.
                    this.ThrowIfDisposed();
                    var started = Environment.TickCount64;
                    var result = this.SynthesizeCore(request, cancellationToken);
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

    private SynthesisResult SynthesizeCore(SynthesisRequest request, CancellationToken ct)
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
        var reference = this.ReferenceFor(referencePath, eng);
        var referenceMs = phase.ElapsedMilliseconds;
        phase.Restart();
        List<long> generated;
        try
        {
            generated = this.Decode(eng, reference, textIds, ct);
            this.log?.Info($"Turbo timing: reference {referenceMs} ms, language model {phase.ElapsedMilliseconds} ms for {generated.Count - 1} tokens.");
        }
        catch (Exception ex) when (this.IsGpuFailure(eng, ex, ct))
        {
            // A managed failure on the GPU means the process is alive: close the crash scope, run this
            // line on the CPU, and stay there until the player retries.
            eng = this.FallBackToCpu(eng, ex);
            reference = this.ReferenceFor(referencePath, eng);
            phase.Restart();
            generated = this.Decode(eng, reference, textIds, ct);
            this.log?.Info($"Turbo timing: language model {phase.ElapsedMilliseconds} ms for {generated.Count - 1} tokens (CPU after a GPU failure).");
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

        this.log?.Info($"Generated {speechOnly.Length} speech tokens (total {speechTokens.Dimensions[1]} with prompt; {eng.Files.Label} on {eng.LmEp}).");
        float[] wav;
        phase.Restart();
        using (var decoderOutputs = RunSession(eng.Decoder, decoderInputs))
        {
            wav = TensorSpan(unwrap<float>(decoderOutputs[0])).ToArray(); // [1, samples] squeezed below
        }

        this.log?.Info($"Turbo timing: decoder {phase.ElapsedMilliseconds} ms for {wav.Length / (double)SampleRate:0.0} s of audio.");

        // np.squeeze(axis=0) + clip to [-1, 1].
        var samples = new float[wav.Length];
        for (var i = 0; i < wav.Length; i++)
        {
            samples[i] = Math.Clamp(wav[i], -1f, 1f);
        }

        return new SynthesisResult(samples, SampleRate);
    }

    private ReferenceEmbeddings ReferenceFor(string referencePath, Engine eng) =>
        this.referenceCache.GetOrAdd($"{eng.Files.Label}|{referencePath}", _ => this.EncodeReference(referencePath, eng));

    private List<long> Decode(Engine eng, ReferenceEmbeddings reference, long[] textIds, CancellationToken ct)
    {
        var generated = eng.Loop.Run(
            new TurboDecodeInput(reference.CondEmb, reference.CondShape[1], textIds),
            ct);

        // The language model survived a whole line on this device: the crash marker has done its job.
        eng.LmGuard?.Succeeded();
        return generated;
    }

    private bool IsGpuFailure(Engine eng, Exception ex, CancellationToken ct) =>
        eng.LmEp != DeviceEp.Cpu
        && this.devices is not null
        && !ct.IsCancellationRequested
        && ex is not (OperationCanceledException or SpeechSynthesisEngineDisposedException or ObjectDisposedException);

    /// <summary>Swaps in a CPU engine after a managed GPU failure. Runs inside the synthesis gate.</summary>
    private Engine FallBackToCpu(Engine failed, Exception ex)
    {
        this.log?.Warn($"The language model failed on the GPU ({ex.GetType().Name}: {ex.Message}); continuing on the CPU.");
        failed.LmGuard?.FailedSafely();
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
        if (this.devices is not { } services)
        {
            return new Resolution(
                TurboFiles.For(TurboPrecision.Fp32), DevicePlan.OnCpu("Running on the CPU."), string.Empty);
        }

        var request = TurboModelSet.Parse(services.Precision?.Invoke());
        Resolution? first = null;
        var note = string.Empty;
        foreach (var precision in TurboModelSet.Candidates(request))
        {
            var files = TurboFiles.For(precision);
            if (precision != TurboPrecision.Fp32 && !files.IsInstalled(this.modelsDir))
            {
                note = $"The {files.Label} voice files are not downloaded, so full precision is used.";
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

        // Unreachable while the fp32 set terminates every candidate list; keep the compiler and a bad catalog honest.
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
        var request = this.devices is { } services ? TurboModelSet.Parse(services.Precision?.Invoke()) : TurboPrecisionRequest.Auto;
        return request == TurboPrecisionRequest.Auto ? TurboFiles.For(TurboPrecision.Fp32) : chosen;
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
        DenseTensor<float> PromptFeat);

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

        public void Dispose()
        {
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
