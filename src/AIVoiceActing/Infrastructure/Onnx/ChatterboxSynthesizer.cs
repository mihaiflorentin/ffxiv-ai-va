namespace AIVoiceActing.Infrastructure.Onnx;

using System.Collections.Concurrent;
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

    private readonly string modelsDir;
    private readonly Func<string, string?> voicePathResolver;
    private readonly Func<ITextTokenizer>? tokenizerFactory;
    private readonly string executionProvider;
    private readonly string? languageModelOverride;
    private readonly ILogSink? log;
    private readonly Lazy<Engine> engine;
    private readonly ConcurrentDictionary<string, ReferenceEmbeddings> referenceCache = new();
    private readonly object tokenizerGate = new();
    private ITextTokenizer? tokenizer;
    private bool tokenizerBroken;

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
        string? languageModelOverride = null)
    {
        this.modelsDir = modelsDir;
        this.voicePathResolver = voicePathResolver;
        var modelsDirLocal = modelsDir;
        this.tokenizerFactory = tokenizerFactory
            ?? (() => new TokenizersDotNetTokenizer(
                Path.Combine(modelsDirLocal, ModelCatalog.TokenizerJsonFileName)));
        this.executionProvider = executionProvider;
        this.languageModelOverride = languageModelOverride;
        this.log = log;
        this.engine = new Lazy<Engine>(this.CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Execution provider the sessions actually run on; populated on first use.</summary>
    public string EffectiveEp => this.engine.IsValueCreated ? this.engine.Value.Ep : "not initialized";

    public bool IsReady
    {
        get
        {
            foreach (var asset in ModelCatalog.ChatterboxRequiredAssets)
            {
                var path = Path.Combine(this.modelsDir, asset.FileName);
                if (!File.Exists(path)
                    || (asset.SizeBytes is { } expected
                        && !FileModelStore.SizeWithinTolerance(new FileInfo(path).Length, expected)))
                {
                    return false;
                }
            }

            // The reference voice must resolve too — no default clip, no synthesis.
            return this.voicePathResolver(ModelCatalog.DefaultVoiceFileName) is not null
                && this.EnsureTokenizer() is not null;
        }
    }

    public async Task<SynthesisResult> SynthesizeAsync(
        SynthesisRequest request,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() => this.SynthesizeCore(request, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Renders paralinguistic tags as "[tag] " prompt prefixes before the text.</summary>
    public static string BuildPromptText(IReadOnlyList<string> tags, string text) =>
        tags.Count == 0
            ? text
            : string.Concat(tags.Select(tag => $"[{tag}] ")) + text;

    public void Dispose()
    {
        if (this.engine.IsValueCreated)
        {
            this.engine.Value.Dispose();
        }
    }

    private SynthesisResult SynthesizeCore(SynthesisRequest request, CancellationToken ct)
    {
        var eng = this.engine.Value;
        var tokenizer = this.EnsureTokenizer()
            ?? throw new SpeechSynthesisException("Tokenizer is unavailable (failed to load).");

        var referencePath = this.voicePathResolver(request.ReferenceVoiceId)
            ?? throw new SpeechSynthesisException(
                $"No reference wav resolves for voice id \"{request.ReferenceVoiceId}\".");
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
        var past = new Dictionary<string, DenseTensor<float>>();

        for (var step = 0; step < MaxNewTokens; step++)
        {
            ct.ThrowIfCancellationRequested();

            float[] textEmbeds;
            using (var embedOutputs = RunSession(
                eng.EmbedTokens,
                EmbedInput(eng.EmbedTokens, embedIds, embedPositions, exaggerationTensor)))
            {
                textEmbeds = TensorSpan(unwrap<float>(embedOutputs[0])).ToArray();
            }

            DenseTensor<float> inputsEmbeds;
            if (step == 0)
            {
                // Condition embedding precedes the text prompt in the sequence.
                var condFrames = reference.CondShape[1];
                var embedFrames = textEmbeds.Length / EmbedWidth(eng.EmbedTokens);
                inputsEmbeds = new DenseTensor<float>([1, condFrames + embedFrames, EmbedWidth(eng.EmbedTokens)]);
                reference.CondEmb.AsSpan().CopyTo(inputsEmbeds.Buffer.Span);
                // Card: zeros [B, 16, 0, 64] per layer/key-value before the first LM step.
                for (var layer = 0; layer < NumHiddenLayers; layer++)
                {
                    past[$"past_key_values.{layer}.key"] = new DenseTensor<float>([1, NumKvHeads, 0, HeadDim]);
                    past[$"past_key_values.{layer}.value"] = new DenseTensor<float>([1, NumKvHeads, 0, HeadDim]);
                }

                attentionMask = new DenseTensor<long>([1, inputsEmbeds.Dimensions[1]]);
                for (var i = 0; i < attentionMask.Dimensions[1]; i++)
                {
                    attentionMask[0, i] = 1;
                }
            }
            else
            {
                inputsEmbeds = new DenseTensor<float>([1, textEmbeds.Length / EmbedWidth(eng.EmbedTokens), EmbedWidth(eng.EmbedTokens)]);
                textEmbeds.AsSpan().CopyTo(inputsEmbeds.Buffer.Span);
            }

            var lmInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("inputs_embeds", inputsEmbeds), NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask) };
            foreach (var kv in past)
            {
                lmInputs.Add(NamedOnnxValue.CreateFromTensor(kv.Key, kv.Value));
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
                // Card: past[key] = present[j] positional zip, layer-major key-then-value.
                var pastNames = past.Keys.ToArray();
                for (var j = 0; j < pastNames.Length; j++)
                {
                    past[pastNames[j]] = CopyTensor(unwrap<float>(lmOutputs[1 + j]));
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
            attentionMask = AppendOnes(attentionMask);
        }

        // speech_tokens = generate_tokens[:, 1:-1] — drops the seeded START and the final
        // token (STOP when the loop broke; the card slices identically when the token cap
        // is exhausted, so the last generated token is discarded in that case too).
        var speechOnly = generated.Skip(1).Take(Math.Max(0, generated.Count - 2)).ToArray();
        var promptTokens = reference.PromptToken;
        var speechTokens = new DenseTensor<long>([1, promptTokens.Length + speechOnly.Length]);
        for (var i = 0; i < promptTokens.Length; i++)
        {
            speechTokens[0, i] = promptTokens[i];
        }

        for (var i = 0; i < speechOnly.Length; i++)
        {
            speechTokens[0, promptTokens.Length + i] = speechOnly[i];
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
                this.tokenizer = this.tokenizerFactory();
            }
            catch (Exception ex)
            {
                // Native tokenizer load failures stay non-fatal for IsReady consumers;
                // synthesis surfaces a clean error instead of crashing the plugin.
                this.tokenizerBroken = true;
                this.log?.Warn($"Tokenizer failed to load: {ex.Message}");
            }

            return this.tokenizer;
        }
    }

    private Engine CreateEngine()
    {
        SessionOptions? options = null;
        InferenceSession encoder, embed, lm, decoder;
        try
        {
            var (epOptions, ep) = EpSelector.CreateSessionOptions(
                this.executionProvider, message => this.log?.Info(message));
            options = epOptions;
            encoder = this.CreateSession(ModelCatalog.SpeechEncoderFileName, epOptions);
            embed = this.CreateSession(ModelCatalog.EmbedTokensFileName, epOptions);
            lm = this.CreateSession(this.languageModelOverride ?? ModelCatalog.LanguageModelQ4FileName, epOptions);
            decoder = this.CreateSession(ModelCatalog.ConditionalDecoderFileName, epOptions);
            this.log?.Info($"Chatterbox sessions initialized on EP '{ep}'.");
            return new Engine(encoder, embed, lm, decoder, ep, options);
        }
        catch (Exception) when (this.executionProvider != "cpu")
        {
            // Provider-level failure at session creation (e.g. CoreML rejects a graph):
            // retry everything on CPU so the engine still works.
            this.log?.Warn("Execution provider rejected a session; retrying on CPU.");
            options?.Dispose();
            var cpuOptions = EpSelector.CreateSessionOptions("cpu").Options;
            encoder = this.CreateSession(ModelCatalog.SpeechEncoderFileName, cpuOptions);
            embed = this.CreateSession(ModelCatalog.EmbedTokensFileName, cpuOptions);
            lm = this.CreateSession(this.languageModelOverride ?? ModelCatalog.LanguageModelQ4FileName, cpuOptions);
            decoder = this.CreateSession(ModelCatalog.ConditionalDecoderFileName, cpuOptions);
            return new Engine(encoder, embed, lm, decoder, "cpu", cpuOptions);
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
        DenseTensor<long> positionIds,
        DenseTensor<float> exaggeration) =>
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("position_ids", positionIds),
            NamedOnnxValue.CreateFromTensor("exaggeration", exaggeration),
        ];

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
        SessionOptions options) : IDisposable
    {
        public InferenceSession Encoder { get; } = encoder;
        public InferenceSession EmbedTokens { get; } = embedTokens;
        public InferenceSession LanguageModel { get; } = languageModel;
        public InferenceSession Decoder { get; } = decoder;
        public string Ep { get; } = ep;
        private SessionOptions Options { get; } = options;

        public void Dispose()
        {
            this.Encoder.Dispose();
            this.EmbedTokens.Dispose();
            this.LanguageModel.Dispose();
            this.Decoder.Dispose();
            this.Options.Dispose();
        }
    }
}
