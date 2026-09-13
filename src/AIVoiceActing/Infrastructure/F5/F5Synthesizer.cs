namespace AIVoiceActing.Infrastructure.F5;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Horus.F5Tts.Onnx;
using Microsoft.ML.OnnxRuntime;

/// <summary>
/// F5-TTS adapter (via Horus.F5Tts.Onnx, MIT): zero-shot voice cloning where the
/// reference clip DEFINES the voice and its emotional delivery — per-race/gender voices
/// are reference clips (<c>voices-f5/&lt;id&gt;.wav</c> + <c>&lt;id&gt;.txt</c> transcript)
/// rather than model-internal speaker banks, so any clip can become a voice. This is the
/// quality tier Kokoro lacks: F5's prosody follows the reference's acting.
/// Model files (fp32 English export, ~1.4 GB total) are provisioned via the Models tab;
/// the DiT transformer runs an iterative denoise loop, so a line takes roughly
/// real-time on CPU — the queue and stale-line drop absorb that.
/// </summary>
public sealed class F5Synthesizer : ISpeechSynthesizer, IDisposable
{
    private const int F5SampleRate = 24000;
    private const string DefaultVoiceId = "uk_male_casual";

    private readonly Func<string> modelsDirFactory;
    private readonly Func<int> intraOpThreadsFactory;
    private readonly Func<string?>? voicesDirFactory;
    private readonly ILogSink? log;

    private readonly object gate = new();
    private F5TtsModel? model;
    private string? initError;
    private bool initializing;
    private bool disposed;
    private readonly Func<string?>? executionProviderFactory;

    public F5Synthesizer(
        Func<string> modelsDirFactory,
        Func<int> intraOpThreadsFactory,
        ILogSink? log = null,
        Func<string?>? voicesDirFactory = null,
        Func<string?>? executionProviderFactory = null)
    {
        this.modelsDirFactory = modelsDirFactory ?? throw new ArgumentNullException(nameof(modelsDirFactory));
        this.intraOpThreadsFactory = intraOpThreadsFactory ?? throw new ArgumentNullException(nameof(intraOpThreadsFactory));
        this.voicesDirFactory = voicesDirFactory;
        this.executionProviderFactory = executionProviderFactory;
        this.log = log;
    }

    public static string PreprocessPathFor(string modelsDir) => Path.Combine(modelsDir, "f5-preprocess.onnx");
    public static string TransformerPathFor(string modelsDir) => Path.Combine(modelsDir, "f5-transformer.onnx");
    public static string DecodePathFor(string modelsDir) => Path.Combine(modelsDir, "f5-decode.onnx");
    public static string VocabPathFor(string modelsDir) => Path.Combine(modelsDir, "f5-vocab.txt");

    /// <summary>
    /// voices.json slots carry Kokoro voice ids; the shipped F5 bank uses clip ids, so
    /// map engine-agnostic ids onto clips. Unknown ids fall back to the default clip.
    /// Child clips are pre-pitched at record time, so VoiceSlot.Pitch stays Kokoro-only.
    /// </summary>
    /// <summary>Kokoro voice id → bundled F5 clip id (shared with the other clip engines).</summary>
    public static string ClipIdFor(string voiceId) =>
        voiceId is not null && ClipAliases.TryGetValue(voiceId, out var clip) ? clip : DefaultVoiceId;

    private static readonly Dictionary<string, string> ClipAliases = new(StringComparer.Ordinal)
    {
        ["bm_lewis"] = "uk_male_casual",
        ["bm_fable"] = "uk_male_casual",
        ["am_eric"] = "uk_male_casual",
        ["default"] = "uk_male_casual",
        ["bm_daniel"] = "uk_male_posh",
        ["bm_george"] = "uk_male_posh",
        ["bf_lily"] = "uk_female_soft",
        ["bf_alice"] = "uk_female_soft",
        ["af_sarah"] = "uk_female_soft",
        ["bf_emma"] = "uk_female_posh",
        ["bf_isabella"] = "uk_female_posh",
        ["am_onyx"] = "deep_male",
        ["am_adam"] = "deep_male",
        ["am_fenrir"] = "deep_male",
        ["af_alloy"] = "deep_female",
        ["af_river"] = "deep_female",
        ["am_liam"] = "child_male",
        ["am_puck"] = "child_male",
        ["af_sky"] = "child_female",
        ["af_bella"] = "child_female",
        ["af_kore"] = "child_female",
    };

    /// <summary>Directory holding the reference-clip bank shipped with the plugin.</summary>
    private string? ResolveClipsDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(F5Synthesizer).Assembly.Location);
        var candidates = new[]
        {
            this.voicesDirFactory?.Invoke(),
            string.IsNullOrEmpty(asmDir) ? null : Path.Combine(asmDir, "voices-f5"),
            Path.Combine(AppContext.BaseDirectory, "voices-f5"),
        };

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate)
            && Directory.Exists(candidate)
            && Directory.EnumerateFiles(candidate, "*.wav").Any());
    }

    public bool IsReady => this.FindNotReadyReason() is null;

    public string NotReadyReason => this.FindNotReadyReason() ?? string.Empty;

    /// <summary>
    /// Readiness means "model files + at least one reference clip present"; the heavy
    /// transformer session builds lazily on first use (or the login pre-warm).
    /// </summary>
    private string? FindNotReadyReason()
    {
        lock (this.gate)
        {
            if (this.model is not null)
            {
                return null;
            }

            if (this.initializing)
            {
                return "Loading F5 model…";
            }

            if (this.initError is { } error)
            {
                return error;
            }

            var modelsDir = this.modelsDirFactory();
            return !File.Exists(TransformerPathFor(modelsDir))
                ? "F5 model not downloaded — use the Models tab."
                : this.ResolveClipsDir() is null
                    ? "F5 reference voices missing — reinstall the plugin."
                    : null;
        }
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        var model = this.EnsureEngine();
        var (clipPath, referenceText) = this.ResolveClip(request.ReferenceVoiceId);

        // F5 renders numbers/symbols poorly: normalize like the training data saw.
        var text = EnglishTextNormalizer.Normalize(StripInlineTags(request.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SpeechSynthesisException("Nothing to speak after normalization.");
        }

        short[] pcm;
        try
        {
            var voice = model.PrepareVoiceFromWav(clipPath, referenceText);
            var result = await voice.SynthesizeAsync(text).ConfigureAwait(false);
            pcm = result.Samples;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SpeechSynthesisException($"F5 synthesis failed for voice \"{request.ReferenceVoiceId}\".", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var samples = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            samples[i] = pcm[i] / 32768f;
        }

        return new SynthesisResult(samples, F5SampleRate);
    }

    /// <summary>Loads the model and runs a short warm-up line; idempotent.</summary>
    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        this.EnsureEngine();
        if (this.IsReady)
        {
            return this.SynthesizeAsync(
                new SynthesisRequest(DefaultVoiceId, "Ready.", Exaggeration: 0, Tags: []),
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.model?.Dispose();
            this.model = null;
        }
    }

    private (string ClipPath, string ReferenceText) ResolveClip(string referenceVoiceId)
    {
        var clipsDir = this.ResolveClipsDir()
            ?? throw new SpeechSynthesisException("F5 reference voices missing — reinstall the plugin.");
        var requested = string.IsNullOrWhiteSpace(referenceVoiceId) ? DefaultVoiceId : referenceVoiceId;
        var id = ClipAliases.TryGetValue(requested, out var clip) ? clip : DefaultVoiceId;
        var wav = Path.Combine(clipsDir, $"{id}.wav");
        var txt = Path.Combine(clipsDir, $"{id}.txt");
        if (!File.Exists(wav) || !File.Exists(txt))
        {
            this.log?.Warn($"F5 clip \"{id}\" missing; falling back to {DefaultVoiceId}.");
            id = DefaultVoiceId;
            wav = Path.Combine(clipsDir, $"{id}.wav");
            txt = Path.Combine(clipsDir, $"{id}.txt");
        }

        if (!File.Exists(wav) || !File.Exists(txt))
        {
            throw new SpeechSynthesisException($"F5 reference clip \"{id}\" not found in {clipsDir}.");
        }

        return (wav, File.ReadAllText(txt).Trim());
    }

    private F5TtsModel EnsureEngine()
    {
        lock (this.gate)
        {
            if (this.model is not null)
            {
                return this.model;
            }

            if (this.disposed)
            {
                throw new SpeechSynthesisException("F5 synthesizer was disposed.");
            }

            var modelsDir = this.modelsDirFactory();
            if (!File.Exists(TransformerPathFor(modelsDir)))
            {
                throw new SpeechSynthesisException("F5 model not downloaded — use the Models tab.");
            }

            this.initializing = true;
            try
            {
                this.log?.Info("Initializing F5 engine (transformer ~1.3 GB; first load may take seconds).");
                var wantGpu = string.Equals(
                    this.executionProviderFactory?.Invoke() ?? "cpu", "directml", StringComparison.OrdinalIgnoreCase);
                F5TtsModel? model = null;
                if (wantGpu)
                {
                    try
                    {
                        model = F5TtsModel.Load(
                            PreprocessPathFor(modelsDir),
                            TransformerPathFor(modelsDir),
                            DecodePathFor(modelsDir),
                            VocabPathFor(modelsDir),
                            configureSession: o =>
                            {
                                o.IntraOpNumThreads = Math.Max(1, this.intraOpThreadsFactory());
                                o.InterOpNumThreads = 1;
                                o.EnableMemoryPattern = true;
                                o.AppendExecutionProvider_DML(0);
                            });
                        this.log?.Info("F5 engine on DirectML GPU.");
                    }
                    catch (Exception ex)
                    {
                        this.log?.Warn($"DirectML unavailable ({ex.Message.Split('\n')[0]}); falling back to CPU.");
                    }
                }

                model ??= F5TtsModel.Load(
                    PreprocessPathFor(modelsDir),
                    TransformerPathFor(modelsDir),
                    DecodePathFor(modelsDir),
                    VocabPathFor(modelsDir),
                    configureSession: o =>
                    {
                        o.IntraOpNumThreads = Math.Max(1, this.intraOpThreadsFactory());
                        o.InterOpNumThreads = 1;
                        o.EnableMemoryPattern = true;
                    });

                this.model = model;
                this.initError = null;
                this.log?.Info("F5 engine ready.");
                return model;
            }
            catch (Exception ex)
            {
                this.initError = $"F5 engine failed to load: {ex.Message}";
                this.log?.Error("F5 engine initialization failed.", ex);
                throw new SpeechSynthesisException(this.initError, ex);
            }
            finally
            {
                this.initializing = false;
            }
        }
    }

    /// <summary>Defensive: inline [tag] text would be read literally by F5.</summary>
    private static string StripInlineTags(string text)
    {
        if (!text.Contains('['))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
