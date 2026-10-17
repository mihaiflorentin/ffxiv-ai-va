namespace AIVoiceActing.Infrastructure.Kokoro;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;

/// <summary>
/// Kokoro-82M adapter (KokoroSharp, MIT): CPU real-time synthesis with 50+ accent voices.
/// The model file (<c>kokoro-v1.0.onnx</c>, fp32) is provisioned via the Models tab; the
/// voice banks ship inside the KokoroSharp package. KokoroWavSynthesizer never touches
/// audio devices (we own playback via NAudioSink) and serializes inference internally.
/// Session options come from the <paramref name="intraOpThreadsFactory"/> CPU-impact
/// knob; phonemization is native C# (MisakiSharp) — no espeak-ng anywhere.
/// </summary>
public sealed class KokoroSynthesizer : ISpeechSynthesizer, IDisposable
{
    private const int KokoroSampleRate = 24000;
    private const string DefaultVoiceName = "af_heart";

    private readonly Func<string> modelsDirFactory;
    private readonly Func<int> intraOpThreadsFactory;
    private readonly Func<string?>? voicesDirFactory;
    private readonly Func<bool>? keepTempoWhenPitched;
    private readonly ILogSink? log;
    private readonly IVoiceLibrary? voiceLibrary;

    /// <summary>Pack style tensors already read from disk, keyed by voice id; stale entries
    /// (file replaced) are detected by length and write time.</summary>
    private readonly Dictionary<string, (string Path, long Length, DateTime Stamp, KokoroVoice Voice)> customVoices = [];

    /// <summary>Guards the small state fields only; never held across a model load, so
    /// readiness checks (read every UI frame) never wait on one.</summary>
    private readonly object gate = new();

    /// <summary>Serializes the model load itself. Nesting order: synthesisGate → loadGate → gate.</summary>
    private readonly object loadGate = new();
    private string? assetReasonCache;
    private long? assetReasonCheckedAt;
    private const long AssetRecheckMs = 2000;

    /// <summary>
    /// Serializes synthesis so Dispose can drain the in-flight line before tearing
    /// down the native session (the in-game engine-switch crash). Nesting order is
    /// always synthesisGate → loadGate → gate, never reversed.
    /// </summary>
    private readonly SemaphoreSlim synthesisGate = new(1, 1);
    private KokoroWavSynthesizer? engine;
    private string? initError;
    private bool initializing;
    private volatile bool disposed;

    public KokoroSynthesizer(
        Func<string> modelsDirFactory,
        Func<int> intraOpThreadsFactory,
        ILogSink? log = null,
        Func<string?>? voicesDirFactory = null,
        Func<bool>? keepTempoWhenPitched = null,
        IVoiceLibrary? voiceLibrary = null)
    {
        this.voiceLibrary = voiceLibrary;
        this.keepTempoWhenPitched = keepTempoWhenPitched;
        this.modelsDirFactory = modelsDirFactory ?? throw new ArgumentNullException(nameof(modelsDirFactory));
        this.intraOpThreadsFactory = intraOpThreadsFactory ?? throw new ArgumentNullException(nameof(intraOpThreadsFactory));
        this.voicesDirFactory = voicesDirFactory;
        this.log = log;
    }

    /// <summary>Absolute path of the Kokoro ONNX model under the models directory.</summary>
    public static string ModelPathFor(string modelsDir) =>
        Path.Combine(modelsDir, ModelCatalog.KokoroModelFileName);

    /// <summary>
    /// First existing voice-bank directory (a folder with voices/*.npy). Dalamud loads
    /// plugin assemblies with an empty Assembly.Location, so the assembly-relative
    /// candidate is dead in-game; the plugin entry wires a factory over
    /// IDalamudPluginInterface.AssemblyLocation (a real path), and the package's own
    /// folder covers SmokeSynth/tests. GetVoice's own auto-load uses the process base
    /// directory (the game folder — a nonexistent drive under Wine), so voices MUST be
    /// loaded explicitly from a candidate that exists.
    /// </summary>
    private string? ResolveVoicesDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(KokoroSynthesizer).Assembly.Location);
        var candidates = new[]
        {
            this.voicesDirFactory?.Invoke(),
            string.IsNullOrEmpty(asmDir) ? null : Path.Combine(asmDir, "voices"),
            Path.Combine(AppContext.BaseDirectory, "voices"),
        };

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate)
            && Directory.Exists(candidate)
            && Directory.EnumerateFiles(candidate, "*.npy").Any());
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

    public bool IsReady => this.FindNotReadyReason() is null;

    public string NotReadyReason => this.FindNotReadyReason() ?? string.Empty;

    /// <summary>
    /// Readiness means "all assets present; a line would synthesize" — NOT "the ONNX
    /// session is already built". The session builds lazily on first use (or the login
    /// pre-warm), so gating the Test tab on construction would deadlock: the buttons
    /// that would trigger the build stay disabled until something else builds it.
    /// </summary>
    private string? FindNotReadyReason()
    {
        lock (this.gate)
        {
            if (this.engine is not null)
            {
                return null;
            }

            if (this.initializing)
            {
                return "Loading Kokoro model…";
            }

            if (this.initError is { } error)
            {
                return error;
            }

            // The disk probe (a stat plus a directory scan) is cached: callers poll this
            // every frame while the window is open.
            var now = Environment.TickCount64;
            if (this.assetReasonCheckedAt is not { } checkedAt || now - checkedAt >= AssetRecheckMs)
            {
                this.assetReasonCache = this.FindMissingAssetReason();
                this.assetReasonCheckedAt = now;
            }

            return this.assetReasonCache;
        }
    }

    private string? FindMissingAssetReason() =>
        !File.Exists(ModelPathFor(this.modelsDirFactory()))
            ? "Kokoro model not downloaded — use the Models tab."
            : this.ResolveVoicesDir() is null
                ? "Kokoro voices missing — reinstall the plugin."
                : null;

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        this.ThrowIfDisposed();
        await this.synthesisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Disposed while queued behind another line.
            this.ThrowIfDisposed();

            var engine = this.EnsureEngine();
            var voice = this.ResolveVoice(request.ReferenceVoiceId);
            var text = StripInlineTags(request.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new SpeechSynthesisException("Nothing to speak after tag stripping.");
            }

            // The emotion plan's exaggeration becomes delivery: Kokoro's prosody is
            // punctuation-driven, so the plan bends terminal punctuation (and pace below).
            text = EmotionPunctuation.Shape(text, Math.Clamp(request.Exaggeration, 0f, 1f));
            // Exaggeration maps to pace: flat reads stay brisk, theatrical ones slow down.
            // The per-speaker slot speed (Lalafell bubbly 1.1) scales on top.
            var speed = request.Speed * (1.05f - (0.20f * Math.Clamp(request.Exaggeration, 0f, 1f)));
            var pitched = Math.Abs(request.Pitch - 1f) > 0.01f;
            if (pitched && this.keepTempoWhenPitched?.Invoke() == true)
            {
                // The pitch resample below scales duration by 1/pitch; render at the
                // compensating pace so a deep voice is not also a slow one.
                speed /= request.Pitch;
            }

            byte[] pcm16;
            try
            {
                // KokoroSharp synthesizes without cancellation support; ct gates the wait below.
                var config = new KokoroTTSPipelineConfig { Speed = speed };
                this.log?.Info($"Kokoro synthesis start: voice \"{voice.Name}\" ({text.Length} chars, speed {speed:0.00}).");
                pcm16 = await engine.SynthesizeAsync(text, voice, config).ConfigureAwait(false);
                this.log?.Info($"Kokoro synthesis complete: {pcm16.Length / 2} samples ({pcm16.Length / 2 / (float)KokoroSampleRate:0.0} s of audio).");
            }
            catch (Exception ex)
            {
                this.log?.Warn($"Kokoro synthesis failed for voice \"{voice.Name}\": {ex.GetType().Name}: {ex.Message}");
                throw new SpeechSynthesisException($"Kokoro synthesis failed for voice \"{voice.Name}\".", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var samples = Pcm16ToFloat(pcm16);
            if (pitched)
            {
                samples = ResamplePitch(samples, request.Pitch);
            }

            return new SynthesisResult(samples, KokoroSampleRate);
        }
        finally
        {
            this.synthesisGate.Release();
        }
    }

    /// <summary>
    /// Loads the model and voices and runs a short warm-up line so the first real line
    /// does not pay session creation or phonemizer dictionary decompression. Safe to
    /// call repeatedly; failures surface through <see cref="NotReadyReason"/>.
    /// </summary>
    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        this.EnsureEngine();
        if (this.IsReady)
        {
            return this.SynthesizeAsync(
                new SynthesisRequest(DefaultVoiceName, "Ready.", Exaggeration: 0, Tags: []),
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
        }

        // Bounded drain of the in-flight line so the native session never dies under
        // inference (the engine-switch AV). New requests throw the marker at
        // ThrowIfDisposed instead of entering the gate.
        var drained = this.synthesisGate.Wait(TimeSpan.FromSeconds(10));
        try
        {
            if (!drained)
            {
                // A line is still inside native inference: freeing the session under it
                // is an access violation in the game process. Leak it instead.
                this.log?.Warn("Kokoro line still running at teardown; leaving its session for the GC.");
                return;
            }

            lock (this.loadGate)
            {
                lock (this.gate)
                {
                    this.engine?.Dispose();
                    this.engine = null;
                }
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

    /// <summary>
    /// Naive pitch shift by resampling: output[i] = input[i * pitch]. Above 1 the sample
    /// stream shortens and plays brighter/faster (the child-voice lift); below 1 it
    /// lengthens and deepens. Linear interpolation; good enough for character colouring.
    /// With keep-tempo on, synthesis already ran at speed / pitch, so only the pitch moves.
    /// </summary>
    private static float[] ResamplePitch(float[] samples, float pitch)
    {
        if (samples.Length == 0)
        {
            return samples;
        }

        var outputLength = Math.Max(1, (int)(samples.Length / pitch));
        var output = new float[outputLength];
        for (var i = 0; i < outputLength; i++)
        {
            var source = i * pitch;
            var i0 = (int)source;
            var i1 = Math.Min(i0 + 1, samples.Length - 1);
            var frac = source - i0;
            output[i] = (samples[i0] * (1f - frac)) + (samples[i1] * frac);
        }

        return output;
    }

    private KokoroWavSynthesizer EnsureEngine()
    {
        lock (this.loadGate)
        {
            lock (this.gate)
            {
                if (this.engine is not null)
                {
                    return this.engine;
                }

                if (this.disposed)
                {
                    throw new SpeechSynthesisEngineDisposedException();
                }

            }

            var modelPath = ModelPathFor(this.modelsDirFactory());
            if (!File.Exists(modelPath))
            {
                throw new SpeechSynthesisException(
                    "Kokoro model not downloaded — use the Models tab.");
            }

            lock (this.gate)
            {
                this.initializing = true;
            }

            try
            {
                var engine = this.LoadEngine(modelPath);
                lock (this.gate)
                {
                    this.engine = engine;
                    this.initError = null;
                }

                this.log?.Info("Kokoro engine ready.");
                return engine;
            }
            catch (Exception ex)
            {
                var error = $"Kokoro engine failed to load: {ex.Message}";
                lock (this.gate)
                {
                    this.initError = error;
                }

                this.log?.Error("Kokoro engine initialization failed.", ex);
                throw new SpeechSynthesisException(error, ex);
            }
            finally
            {
                lock (this.gate)
                {
                    this.initializing = false;
                    this.assetReasonCheckedAt = null;
                }
            }
        }
    }

    /// <summary>Builds the ONNX session and loads the voice bank. Runs under
    /// <see cref="loadGate"/> only, so readiness polls stay non-blocking meanwhile.</summary>
    private KokoroWavSynthesizer LoadEngine(string modelPath)
    {
        var options = new SessionOptions
        {
            // CPU-impact knob: bounded intra-op parallelism protects the frame rate.
            IntraOpNumThreads = Math.Max(1, this.intraOpThreadsFactory()),
            InterOpNumThreads = 1,
            EnableMemoryPattern = true,
        };

        this.log?.Info($"Initializing Kokoro engine ({modelPath}, intra-op threads: {options.IntraOpNumThreads}).");
        var engine = KokoroWavSynthesizer.LoadModel(modelPath, options);

        if (this.ResolveVoicesDir() is { } voicesDir)
        {
            KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
            try
            {
                // Latch init on an unreadable bank: GetVoice's own auto-load hits
                // the game dir under Wine and spits hundreds of errors per line.
                _ = KokoroVoiceManager.GetVoice(DefaultVoiceName);
            }
            catch (Exception ex)
            {
                engine.Dispose();
                throw new SpeechSynthesisException($"Kokoro voices unreadable at {voicesDir}: {ex.Message}");
            }

            this.log?.Info($"Kokoro voices loaded from {voicesDir}.");
        }
        else
        {
            this.log?.Warn("Kokoro voices directory not found; synthesis will fail until the plugin is reinstalled.");
        }

        return engine;
    }

    /// <summary>
    /// Picks the Kokoro voice for an id. Pack voices carry a style tensor on disk, which is
    /// registered under an internal name whose first letter selects the phonemizer the
    /// manifest declared (KokoroSharp derives the G2P language from that letter); stock
    /// voices are looked up by name in the loaded bank. Anything missing, unavailable or
    /// unreadable falls back to <see cref="DefaultVoiceName"/> with a log line.
    /// </summary>
    internal KokoroVoice ResolveVoice(string referenceVoiceId)
    {
        var name = string.IsNullOrWhiteSpace(referenceVoiceId) ? DefaultVoiceName : referenceVoiceId;
        try
        {
            if (this.voiceLibrary?.Find(name) is { } entry)
            {
                if (!entry.Available)
                {
                    throw new InvalidOperationException(entry.UnavailableReason ?? "voice is unavailable");
                }

                if (this.LoadCustomVoice(entry) is { } custom)
                {
                    return custom;
                }
            }

            return KokoroVoiceManager.GetVoice(name);
        }
        catch (Exception ex)
        {
            this.log?.Warn($"Kokoro voice \"{name}\" unavailable ({ex.Message}); falling back to {DefaultVoiceName}.");
            return KokoroVoiceManager.GetVoice(DefaultVoiceName);
        }
    }

    /// <summary>The voice's on-disk style tensor as a Kokoro voice under its internal name, or
    /// null for a stock voice (no file of its own).</summary>
    private KokoroVoice? LoadCustomVoice(VoiceInfo entry)
    {
        var realization = entry.Realizations.FirstOrDefault(r =>
            r.Kind == RealizationKind.StyleTensor
            && r.Available
            && r.Path.Length > 0
            && string.Equals(r.Engine, "kokoro", StringComparison.OrdinalIgnoreCase));
        if (realization is null)
        {
            return null;
        }

        var file = new FileInfo(realization.Path);
        lock (this.customVoices)
        {
            if (this.customVoices.TryGetValue(entry.Id, out var cached)
                && cached.Path == realization.Path
                && cached.Length == file.Length
                && cached.Stamp == file.LastWriteTimeUtc)
            {
                return cached.Voice;
            }

            var phonemizer = Phonemizers.Normalize(realization.Phonemizer) ?? Phonemizers.EnglishUs;
            var features = KokoroVoice.FromPath(realization.Path).Features;
            var voice = new KokoroVoice
            {
                Name = Phonemizers.InternalName(entry.Id, phonemizer, entry.Sex),
                Features = features,
            };
            this.customVoices[entry.Id] = (realization.Path, file.Length, file.LastWriteTimeUtc, voice);
            this.log?.Info($"Kokoro pack voice \"{entry.Id}\" loaded as \"{voice.Name}\" (phonemizer {phonemizer}).");
            return voice;
        }
    }

    /// <summary>Defensive: inline [tag] text would be phonemized literally by Kokoro.</summary>
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

    private static float[] Pcm16ToFloat(byte[] pcm16)
    {
        var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm16);
        var samples = new float[shorts.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = shorts[i] / 32768f;
        }

        return samples;
    }
}
