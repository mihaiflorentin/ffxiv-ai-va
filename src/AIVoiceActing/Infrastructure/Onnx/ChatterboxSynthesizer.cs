namespace AIVoiceActing.Infrastructure.Onnx;

using System.Collections.Concurrent;
using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
/// <summary>
/// Chatterbox ONNX speech synthesizer — a faithful transcription of the reference recipe on
/// the model card (https://huggingface.co/onnx-community/chatterbox-ONNX), with each
/// deviation from the Python called out in a comment at the deviation site:
/// four sessions (speech_encoder → embed_tokens → language_model_q4 greedy KV-cache loop →
/// conditional_decoder), START/STOP speech tokens 6561/6562, repetition penalty 1.2
/// (asymmetric: negative ×penalty, positive ÷penalty), max 256 new tokens, 24 kHz mono out.
/// The PerTh watermarker is absent from the ONNX graphs (card: optional upstream post-step),
/// so output is unwatermarked by construction.
/// </summary>
public sealed class ChatterboxSynthesizer : ISpeechSynthesizer, IDisposable
{
    private const int StartSpeechToken = 6561;
    private const int StopSpeechToken = 6562;
    private const int MaxNewTokens = 256; // card: generation_config.json default max_new_tokens
    private const int MinSpeechTokens = 30; // see DEVIATION note in the generation loop
    private const float RepetitionPenalty = 1.2f;
    private const int NumHiddenLayers = 30;
    private const int NumKvHeads = 16;
    private const int HeadDim = 64;
    private const int SampleRate = 24000;
    private const int TurboSilenceToken = 4299; // Turbo reference: 3 appended before decode

    /// <summary>
    /// Per-export protocol differences the shared generation loop branches on. The
    /// legacy community export feeds position_ids+exaggeration to embed_tokens and
    /// derives LM positions internally; the official Turbo export has a bare
    /// input_ids embed, an LM that requires explicit position_ids (arange over the
    /// condition+text prefix, then last+1 per step), 24 layers, and appends three
    /// SILENCE tokens before decoding.
    /// </summary>
    private sealed record Variant(
        string EncoderFile,
        string EmbedFile,
        string LmFile,
        string DecoderFile,
        string TokenizerFile,
        int Layers,
        bool EmbedTakesPositions,
        bool EmbedTakesExaggeration,
        bool LmTakesPositions,
        int SilenceSuffixCount);

    private static readonly Variant LegacyVariant = new(
        ModelCatalog.SpeechEncoderFileName,
        ModelCatalog.EmbedTokensFileName,
        ModelCatalog.LanguageModelQ4FileName,
        ModelCatalog.ConditionalDecoderFileName,
        ModelCatalog.TokenizerJsonFileName,
        Layers: NumHiddenLayers,
        EmbedTakesPositions: true,
        EmbedTakesExaggeration: true,
        LmTakesPositions: false,
        SilenceSuffixCount: 0);

    private static readonly Variant TurboVariant = new(
        ModelCatalog.TurboSpeechEncoderFileName,
        ModelCatalog.TurboEmbedTokensFileName,
        ModelCatalog.TurboLanguageModelFileName,
        ModelCatalog.TurboConditionalDecoderFileName,
        ModelCatalog.TurboTokenizerJsonFileName,
        Layers: 24, // config.json text_config.n_layer
        EmbedTakesPositions: false,
        EmbedTakesExaggeration: false,
        LmTakesPositions: true,
        SilenceSuffixCount: 3);

    private readonly string modelsDir;
    private readonly Func<string, string?> voicePathResolver;
    private readonly Func<ITextTokenizer>? tokenizerFactory;
    private readonly string executionProvider;
    private readonly string? languageModelOverride;
    private readonly Variant variant;
    private readonly ILogSink? log;
    private readonly int? intraOpThreads;
    private readonly TurboDeviceServices? devices;
    private readonly object statusGate = new();
    private volatile Lazy<Engine> engine;
    private DeviceMode? modeOverride;
    private volatile string? loadFailure;
    private volatile bool loading;
    // Unbounded by design: one entry is a few MB (condition embedding + prompt tokens +
    // speaker tensors) and the voice population per session is small (tens — race/gender
    // slots plus overrides), so the cache stays in the low MBs for the process lifetime;
    // no eviction until a real workload shows otherwise.
    private readonly ConcurrentDictionary<string, ReferenceEmbeddings> referenceCache = new();
    private readonly object tokenizerGate = new();
    private ITextTokenizer? tokenizer;
    private bool tokenizerBroken;
    private string? tokenizerFailure;

    /// <param name="modelsDir">Directory with the downloaded catalog assets.</param>
    /// <param name="voicePathResolver">
    /// ReferenceVoiceId → wav path (smoke CLI maps every id to --ref; the plugin wires
    /// the RaceVoiceMap/bundled clips later).
    /// </param>
    /// <param name="tokenizerFactory">Tokenizer seam; defaults to Tokenizers.DotNet over modelsDir/tokenizer.json.</param>
    /// <param name="executionProvider">"auto"|"coreml"|"cpu"|"directml" (see EpSelector).</param>
    /// <param name="languageModelOverride">File name of the LM session under modelsDir (q4 default; fp32 fallback).</param>
    public ChatterboxSynthesizer(
        string modelsDir,
        Func<string, string?> voicePathResolver,
        Func<ITextTokenizer>? tokenizerFactory = null,
        string executionProvider = "auto",
        ILogSink? log = null,
        string? languageModelOverride = null,
        int? intraOpThreads = null)
        : this(LegacyVariant, modelsDir, voicePathResolver, tokenizerFactory, executionProvider, log, languageModelOverride, intraOpThreads, null)
    {
    }

    /// <summary>
    /// Chatterbox Turbo variant (official ResembleAI ONNX export). With <paramref name="devices"/> the
    /// sessions follow the device plan (language model only; see <see cref="TurboDeviceServices.LmGpuLoopReady"/>);
    /// without it the legacy <paramref name="executionProvider"/> string applies to all four.
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
            TurboVariant,
            modelsDir,
            voicePathResolver,
            tokenizerFactory ?? TurboTokenizerFactory(modelsDir),
            executionProvider,
            log,
            null,
            intraOpThreads,
            devices);


    private static Func<ITextTokenizer> TurboTokenizerFactory(string modelsDir) =>
        () => new TokenizersDotNetTokenizer(Path.Combine(modelsDir, ModelCatalog.TurboTokenizerJsonFileName));

    /// <summary>True when this instance runs the Turbo export protocol.</summary>
    public bool IsTurbo => this.variant == TurboVariant;

    private ChatterboxSynthesizer(
        Variant variant,
        string modelsDir,
        Func<string, string?> voicePathResolver,
        Func<ITextTokenizer>? tokenizerFactory,
        string executionProvider,
        ILogSink? log,
        string? languageModelOverride,
        int? intraOpThreads,
        TurboDeviceServices? devices)
    {
        this.devices = variant == TurboVariant ? devices : null;
        this.variant = variant;
        this.modelsDir = modelsDir;
        this.voicePathResolver = voicePathResolver;
        var modelsDirLocal = modelsDir;
        this.tokenizerFactory = tokenizerFactory
            ?? (() => new TokenizersDotNetTokenizer(
                Path.Combine(modelsDirLocal, ModelCatalog.TokenizerJsonFileName)));
        this.executionProvider = executionProvider;
        this.languageModelOverride = languageModelOverride;
        this.intraOpThreads = intraOpThreads;
        this.log = log;
        this.engine = new Lazy<Engine>(this.CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Execution provider the sessions actually run on; populated on first use.</summary>
    public string EffectiveEp => this.engine.IsValueCreated ? this.engine.Value.Ep : "not initialized";

    public bool IsReady => this.FindNotReadyReason() is null;

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
    /// Device state for the UI. Turbo with device services reports where the language model really
    /// runs and why; everything else is plain ready / not installed on the CPU. Cheap enough to
    /// poll per frame: it never hashes files or touches the GPU runtime.
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
                requested = this.modeOverride ?? services.Planner.Preferences.Mode;
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

            // Not loaded yet: preview the plan without hashing or loading anything.
            var lmFile = this.languageModelOverride ?? this.variant.LmFile;
            var plan = services.Planner.PlanFor(
                DeviceModePolicy.TurboEngineId, this.modelsDir, lmFile, cheap: true, this.ModeOverride);
            return StatusFor(requested, plan, services, services.Planner.Preferences.GpuDisabledByCrashGuard, gpuFailed: false);
        }
    }

    private DeviceMode? ModeOverride
    {
        get
        {
            lock (this.statusGate)
            {
                return this.modeOverride;
            }
        }
    }

    /// <summary>
    /// Maps a plan to a user-facing status. Until the decode loop supports it (see
    /// <see cref="TurboDeviceServices.LmGpuLoopReady"/>) a GPU plan is reported as a CPU run.
    /// </summary>
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
                "Chatterbox Turbo runs on the CPU for now; GPU speech for it is coming in a later update.",
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
    /// flag. Waits for the line in flight (single-flight gate), then loads again, so it is safe to
    /// call from a button.
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
        // The LM actually in use: an override (e.g. the fp32 fallback) replaces the
        // catalog's q4 pair, so readiness must check that one, not the ignored default.
        var required = this.variant == TurboVariant
            ? ModelCatalog.TurboRequiredAssets
            : ModelCatalog.ChatterboxRequiredAssets;
        foreach (var asset in required)
        {
            if (this.languageModelOverride is not null
                && (asset.FileName == ModelCatalog.LanguageModelQ4FileName
                    || asset.FileName == ModelCatalog.LanguageModelQ4DataFileName))
            {
                continue;
            }

            // The default voice is existence-checked below via the resolver: the bundled
            // clip is a PCM16 conversion of the pinned HF download, so its byte size
            // legitimately differs from the catalog pin (any playable wav is fine).
            if (asset.FileName == ModelCatalog.DefaultVoiceFileName)
            {
                continue;
            }

            if (!this.AssetPresent(asset.FileName, asset.SizeBytes))
            {
                return $"Missing model {asset.FileName} — Models tab → Download all missing.";
            }
        }

        var lmInUse = this.languageModelOverride ?? this.variant.LmFile;
        if (!this.AssetPresent(lmInUse, null))
        {
            return $"Missing model {lmInUse} — Models tab → Download all missing.";
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
    // readiness-specific variant additionally honors the LM override and catalog size
    // fallback per session, so it stays local to the engine.
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

    // Single-flight: one synthesis at a time. Concurrent CPU runs pin the machine and
    // multiply KV-cache churn (the 40 GB incident); queued requests hold only their
    // request object until the gate frees.
    private readonly SemaphoreSlim synthesisGate = new(1, 1);
    private volatile bool disposed;

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
        var reference = this.referenceCache.GetOrAdd(
            referencePath,
            _ => this.EncodeReference(referencePath, eng));
        ct.ThrowIfCancellationRequested();

        // exaggeration comes from the emotion plan via the request; tags arrive as prompt prefixes.
        var promptText = BuildPromptText(request.Tags, request.Text);
        var exaggeration = Math.Clamp(request.Exaggeration, 0f, 1f);

        // Tokenize text (ids < 6561).
        var textIds = tokenizer.Encode(promptText);
        var inputIds = new DenseTensor<long>([1, textIds.Length]);
        for (var i = 0; i < textIds.Length; i++)
        {
            inputIds[0, i] = textIds[i];
        }

        // position_ids: 0 for any id >= START, else index-1 — transcribed verbatim from the
        // card (np.where(input_ids >= START, 0, arange(N) - 1)); the -1 start is intentional
        // there (the prompt sits after the condition embedding, which owns position 0).
        var positionIds = new DenseTensor<long>([1, textIds.Length]);
        for (var i = 0; i < textIds.Length; i++)
        {
            positionIds[0, i] = textIds[i] >= StartSpeechToken ? 0 : i - 1;
        }

        var exaggerationTensor = new DenseTensor<float>([1]);
        exaggerationTensor[0] = exaggeration;
        var embedIds = inputIds;
        var embedPositions = positionIds;

        // Greedy generation state.
        var generated = new List<long> { StartSpeechToken }; // card: generate_tokens = [[6561]]
        DenseTensor<long> attentionMask = null!;
        DenseTensor<long>? lmPositionIds = null; // Turbo: required LM input each step
        long turboLastPosition = 0;
        // KV cache MUST be an ordered list: the card relies on Python dict insertion
        // order (layer-major, key-then-value) to zip the model's positional present
        // outputs back by name. A .NET Dictionary iterates in hash order, which
        // scrambles the layers and feeds the model garbage from step 2 on — the
        // generation never emits STOP and the decoder renders noise.
        var pastNames = new List<string>(this.variant.Layers * 2);
        var pastTensors = new List<DenseTensor<float>>(this.variant.Layers * 2);

        for (var step = 0; step < MaxNewTokens; step++)
        {
            ct.ThrowIfCancellationRequested();

            float[] textEmbeds;
            using (var embedOutputs = RunSession(
                eng.EmbedTokens,
                EmbedInput(eng.EmbedTokens, embedIds, this.variant.EmbedTakesPositions ? embedPositions : null, this.variant.EmbedTakesExaggeration ? exaggerationTensor : null)))
            {
                textEmbeds = TensorSpan(unwrap<float>(embedOutputs[0])).ToArray();
            }

            DenseTensor<float> inputsEmbeds;
            if (step == 0)
            {
                var condFrames = reference.CondShape[1];
                var embedFrames = textEmbeds.Length / EmbedWidth(eng.EmbedTokens);
                inputsEmbeds = new DenseTensor<float>([1, condFrames + embedFrames, EmbedWidth(eng.EmbedTokens)]);
                // Card: np.concatenate((cond_emb, inputs_embeds), axis=1) — the condition
                // embedding precedes the text prompt, and BOTH are written into the buffer.
                reference.CondEmb.AsSpan().CopyTo(inputsEmbeds.Buffer.Span);
                textEmbeds.AsSpan().CopyTo(
                    inputsEmbeds.Buffer.Span[(condFrames * EmbedWidth(eng.EmbedTokens))..]);
                // Card: zeros [B, 16, 0, 64] per layer/key-value before the first LM step.
                for (var layer = 0; layer < this.variant.Layers; layer++)
                {
                    pastNames.Add($"past_key_values.{layer}.key");
                    pastTensors.Add(new DenseTensor<float>([1, NumKvHeads, 0, HeadDim]));
                    pastNames.Add($"past_key_values.{layer}.value");
                    pastTensors.Add(new DenseTensor<float>([1, NumKvHeads, 0, HeadDim]));
                }

                attentionMask = new DenseTensor<long>([1, inputsEmbeds.Dimensions[1]]);
                for (var i = 0; i < attentionMask.Dimensions[1]; i++)
                {
                    attentionMask[0, i] = 1;
                }

                if (this.variant.LmTakesPositions)
                {
                    // Turbo reference: position_ids = arange(seq_len) over cond+text.
                    turboLastPosition = inputsEmbeds.Dimensions[1] - 1;
                    lmPositionIds = new DenseTensor<long>([1, inputsEmbeds.Dimensions[1]]);
                    for (var i = 0; i < lmPositionIds.Dimensions[1]; i++)
                    {
                        lmPositionIds[0, i] = i;
                    }
                }
            }
            else
            {
                inputsEmbeds = new DenseTensor<float>([1, textEmbeds.Length / EmbedWidth(eng.EmbedTokens), EmbedWidth(eng.EmbedTokens)]);
                textEmbeds.AsSpan().CopyTo(inputsEmbeds.Buffer.Span);
            }

            var lmInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("inputs_embeds", inputsEmbeds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            };
            if (lmPositionIds is not null)
            {
                lmInputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", lmPositionIds));
            }
            for (var j = 0; j < pastNames.Count; j++)
            {
                lmInputs.Add(NamedOnnxValue.CreateFromTensor(pastNames[j], pastTensors[j]));
            }

            float[] lastLogits;
            using (var lmOutputs = RunSession(eng.LanguageModel, lmInputs))
            {
                // Card: logits, *present = run(...); past[key] = present[j] — positional zip
                // over the same canonical layer-major (key, value) order the card iterates.
                var logitsTensor = unwrap<float>(lmOutputs[0]);
                var vocab = logitsTensor.Dimensions[^1];
                lastLogits = TensorSpan(logitsTensor)[^vocab..].ToArray();
                // ORT owns output memory — clone the present tensors before the results
                // collection is disposed (numpy gives the Python card this for free).
                // Positional zip over the SAME ordered names the inputs were built from —
                // the ONNX graph returns present tensors in that layer-major order.
                for (var j = 0; j < pastNames.Count; j++)
                {
                    pastTensors[j] = CopyTensor(unwrap<float>(lmOutputs[1 + j]));
                }
            }

            ApplyRepetitionPenalty(lastLogits, generated, RepetitionPenalty);
            // DEVIATION (documented): on short prompts this export can emit STOP as its very
            // first token, yielding zero speech tokens and no audio. FFXIV dialogue lines are
            // short, so while under MinSpeechTokens (~1 s of speech) the STOP logit is masked
            // to guarantee a minimum utterance; long prompts stop naturally after it.
            if (generated.Count - 1 < MinSpeechTokens)
            {
                lastLogits[StopSpeechToken] = float.NegativeInfinity;
            }

            var nextToken = Argmax(lastLogits);
            generated.Add(nextToken);
            if (nextToken == StopSpeechToken)
            {
                break;
            }

            // Embed the newly sampled token for the next step (card: position_ids = [[i+1]]).
            embedIds = new DenseTensor<long>([1, 1]);
            embedIds[0, 0] = nextToken;
            embedPositions = new DenseTensor<long>([1, 1]);
            embedPositions[0, 0] = step + 1;
            if (this.variant.LmTakesPositions)
            {
                // Turbo reference: position_ids = position_ids[:, -1:] + 1.
                turboLastPosition++;
                lmPositionIds = new DenseTensor<long>([1, 1]);
                lmPositionIds[0, 0] = turboLastPosition;
            }
            attentionMask = AppendOnes(attentionMask);
        }

        // speech_tokens = generate_tokens[:, 1:-1] — drops the seeded START and the final
        // token (STOP when the loop broke; the card slices identically when the token cap
        // is exhausted, so the last generated token is discarded in that case too).
        var speechOnly = generated.Skip(1).Take(Math.Max(0, generated.Count - 2)).ToArray();
        var promptTokens = reference.PromptToken;
        var speechTokens = new DenseTensor<long>([1, promptTokens.Length + speechOnly.Length + this.variant.SilenceSuffixCount]);
        for (var i = 0; i < promptTokens.Length; i++)
        {
            speechTokens[0, i] = promptTokens[i];
        }

        for (var i = 0; i < speechOnly.Length; i++)
        {
            speechTokens[0, promptTokens.Length + i] = speechOnly[i];
        }

        if (this.variant.SilenceSuffixCount > 0)
        {
            // Turbo reference: silence_tokens = full((n, 3), SILENCE) appended at the end.
            for (var i = 0; i < this.variant.SilenceSuffixCount; i++)
            {
                speechTokens[0, speechTokens.Dimensions[1] - this.variant.SilenceSuffixCount + i] = TurboSilenceToken;
            }
        }

        var decoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("speech_tokens", speechTokens),
            NamedOnnxValue.CreateFromTensor("speaker_embeddings", reference.RefXVector),
            NamedOnnxValue.CreateFromTensor("speaker_features", reference.PromptFeat),
        };

        this.log?.Info($"Generated {speechOnly.Length} speech tokens (total {speechTokens.Dimensions[1]} with prompt).");
        float[] wav;
        using (var decoderOutputs = RunSession(eng.Decoder, decoderInputs))
        {
            wav = TensorSpan(unwrap<float>(decoderOutputs[0])).ToArray(); // [1, samples] squeezed below
        }

        // np.squeeze(axis=0) + clip to [-1, 1].
        var samples = new float[wav.Length];
        for (var i = 0; i < wav.Length; i++)
        {
            samples[i] = Math.Clamp(wav[i], -1f, 1f);
        }

        return new SynthesisResult(samples, SampleRate);
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

    private Engine CreateEngine()
    {
        this.loading = true;
        try
        {
            return this.devices is { } services ? this.CreateTurboEngine(services) : this.CreateLegacyEngine();
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
        var lmFile = this.languageModelOverride ?? this.variant.LmFile;
        var requested = this.ModeOverride ?? services.Planner.Preferences.Mode;
        var crashFlag = services.Planner.Preferences.GpuDisabledByCrashGuard;
        var plan = services.Planner.PlanFor(DeviceModePolicy.TurboEngineId, this.modelsDir, lmFile, cheap: false, this.ModeOverride);
        this.log?.Info($"Chatterbox Turbo device plan: {plan.Ep} ({plan.Reason})");

        var gpuFailed = false;
        if (plan.IsGpu && services.LmGpuLoopReady)
        {
            try
            {
                var engine = this.BuildSessions(services, plan, lmFile);
                engine.DeviceStatus = engine.LmEp == DeviceEp.Cpu
                    ? StatusFor(requested, DevicePlan.OnCpu(engine.FallbackReason, wantedGpu: true), services, crashFlag, gpuFailed: true)
                    : StatusFor(requested, plan, services, crashFlag, gpuFailed: false);
                return engine;
            }
            catch (Exception ex)
            {
                this.log?.Warn($"Turbo sessions failed on the GPU ({ex.Message}); retrying everything on the CPU.");
                gpuFailed = true;
            }
        }

        // CPU build: the plan said so, the decode loop is not GPU-ready yet, or the GPU build failed.
        var cpuPlan = plan.IsGpu
            ? DevicePlan.OnCpu(plan.Reason, wantedGpu: true, plan.PowerPreference)
            : plan;
        var cpuEngine = this.BuildSessions(services, cpuPlan, lmFile);
        cpuEngine.DeviceStatus = gpuFailed
            ? new EngineStatus(
                EngineState.FellBack,
                requested,
                ActiveDevice.Cpu,
                "The GPU could not be started, so Chatterbox Turbo runs on the CPU.",
                CanRetryGpu: true)
            : StatusFor(requested, plan, services, crashFlag, gpuFailed: false);
        return cpuEngine;
    }

    private Engine BuildSessions(TurboDeviceServices services, DevicePlan plan, string lmFile)
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

            var encoder = this.CreateSession(this.variant.EncoderFile, Build(SessionRole.Encoder).Options);
            sessions.Add(encoder);
            var embed = this.CreateSession(this.variant.EmbedFile, Build(SessionRole.Embedding).Options);
            sessions.Add(embed);
            var lmBuild = Build(SessionRole.Lm);
            var lm = this.CreateSession(lmFile, lmBuild.Options);
            sessions.Add(lm);
            var decoder = this.CreateSession(this.variant.DecoderFile, Build(SessionRole.Decoder).Options);
            sessions.Add(decoder);
            this.log?.Info($"Chatterbox Turbo sessions initialized (language model on {lmBuild.Ep}).");
            return new Engine(
                encoder, embed, lm, decoder, lmBuild.Ep.ToString().ToLowerInvariant(), builds.Select(b => b.Options).ToArray())
            {
                LmEp = lmBuild.Ep,
                FallbackReason = lmBuild.FallbackReason,
                LmGuard = lmBuild.Guard,
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

    private Engine CreateLegacyEngine()
    {
        SessionOptions? options = null;
        InferenceSession encoder, embed, lm, decoder;
        var created = new List<InferenceSession>(); // disposed if a later session fails
        try
        {
            var (epOptions, ep) = EpSelector.CreateSessionOptions(
                this.executionProvider,
                message => this.log?.Info(message),
                this.intraOpThreads);
            options = epOptions;
            encoder = this.CreateSession(this.variant.EncoderFile, epOptions);
            created.Add(encoder);
            embed = this.CreateSession(this.variant.EmbedFile, epOptions);
            created.Add(embed);
            lm = this.CreateSession(this.languageModelOverride ?? this.variant.LmFile, epOptions);
            created.Add(lm);
            decoder = this.CreateSession(this.variant.DecoderFile, epOptions);
            created.Add(decoder);
            this.log?.Info($"Chatterbox sessions initialized on EP '{ep}'.");
            return new Engine(encoder, embed, lm, decoder, ep, [options]);
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
            encoder = this.CreateSession(this.variant.EncoderFile, cpuOptions);
            embed = this.CreateSession(this.variant.EmbedFile, cpuOptions);
            lm = this.CreateSession(this.languageModelOverride ?? this.variant.LmFile, cpuOptions);
            decoder = this.CreateSession(this.variant.DecoderFile, cpuOptions);
            return new Engine(encoder, embed, lm, decoder, "cpu", [cpuOptions]);
        }
    }

    private InferenceSession CreateSession(string fileName, SessionOptions options)
    {
        var path = Path.Combine(this.modelsDir, fileName);
        if (!File.Exists(path))
        {
            throw new SpeechSynthesisException($"Model file missing: {fileName} (download it from the Models tab).");
        }

        return new InferenceSession(path, options);
    }

    private static IReadOnlyList<NamedOnnxValue> EmbedInput(
        InferenceSession embedSession,
        DenseTensor<long> inputIds,
        DenseTensor<long>? positionIds,
        DenseTensor<float>? exaggeration)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
        };
        if (positionIds is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("position_ids", positionIds));
        }

        if (exaggeration is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("exaggeration", exaggeration));
        }

        return inputs;
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

    private static int EmbedWidth(InferenceSession embedSession)
    {
        // inputs_embeds width from the embed session's output metadata (1024 for chatterbox).
        var meta = embedSession.OutputMetadata.Values.First();
        return meta.Dimensions[^1];
    }

    private static DenseTensor<T> CopyTensor<T>(Tensor<T> tensor)
        where T : unmanaged
    {
        var copy = new DenseTensor<T>([.. tensor.Dimensions]);
        TensorSpan(tensor).CopyTo(copy.Buffer.Span);
        return copy;
    }

    private static DenseTensor<long> AppendOnes(DenseTensor<long> mask)
    {
        var next = new DenseTensor<long>([1, mask.Dimensions[1] + 1]);
        mask.Buffer.Span.CopyTo(next.Buffer.Span);
        next.Buffer.Span[mask.Dimensions[1]] = 1;
        return next;
    }

    /// <summary>
    /// Card's RepetitionPenaltyLogitsProcessor: score &lt; 0 → ×penalty, else ÷penalty,
    /// applied over the generated history (duplicate ids converge to the same value, so
    /// distinct ids reproduce the card's np.put_along_axis result).
    /// </summary>
    private static void ApplyRepetitionPenalty(float[] logits, List<long> generated, float penalty)
    {
        foreach (var token in generated.Distinct())
        {
            ref var score = ref logits[(int)token];
            score = score < 0 ? score * penalty : score / penalty;
        }
    }

    private static long Argmax(ReadOnlySpan<float> logits)
    {
        var best = 0;
        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < logits.Length; i++)
        {
            if (logits[i] > bestScore)
            {
                bestScore = logits[i];
                best = i;
            }
        }

        return best;
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
        IReadOnlyList<SessionOptions> options) : IDisposable
    {
        public InferenceSession Encoder { get; } = encoder;
        public InferenceSession EmbedTokens { get; } = embedTokens;
        public InferenceSession LanguageModel { get; } = languageModel;
        public InferenceSession Decoder { get; } = decoder;
        public string Ep { get; } = ep;
        private IReadOnlyList<SessionOptions> Options { get; } = options;

        /// <summary>Turbo with device services only: what the UI shows for this build.</summary>
        public EngineStatus? DeviceStatus { get; set; }

        /// <summary>Provider the language model session got.</summary>
        public DeviceEp LmEp { get; init; } = DeviceEp.Cpu;

        /// <summary>Why a wanted GPU session became a CPU one; empty otherwise.</summary>
        public string FallbackReason { get; init; } = string.Empty;

        /// <summary>
        /// Crash-guard scope of a GPU language model session. The decode loop completes it after the
        /// first successful inference (and fails it on a managed exception); until then the marker
        /// stays on disk. Null on the CPU.
        /// </summary>
        public GpuCrashGuard.Scope? LmGuard { get; init; }

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

            // A scope still open here never saw its first inference; the process is alive, so
            // nothing crashed and the marker must not condemn the next start.
            this.LmGuard?.FailedSafely();
        }
    }
}
